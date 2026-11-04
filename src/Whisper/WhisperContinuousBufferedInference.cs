using System.Runtime.CompilerServices;
using CommunityToolkit.Diagnostics;
using NAudio.Wave;
using OwlCore.AI.Inference;
using OwlCore.ComponentModel;
using OwlCore.Diagnostics;
using OwlCore.Extensions;
using OwlCore.Storage;
using OwlCore.Storage.Memory;
using Whisper.net;
using SoundFlow.Components;
using SoundFlow.Structs;
using SoundFlow.Enums;
using System.Runtime.InteropServices;
using Silk.NET.OpenAL;
using SoundFlow.Utils;

namespace OwlCore.Console.TalkBox.Whisper;

/// <summary>
/// Holds an audio file and an inference segment. 
/// </summary>
/// <param name="Data"></param>
/// <param name="AudioFile"></param>
public record AudioFileInferenceSegment(SegmentData Data, IFile AudioFile);

/// <summary>
/// Continuous Whisper inference from an <see cref="IFile"/> whose stream implements <see cref="IWaveProvider"/>. 
/// </summary>
/// <remarks>
/// If it takes longer to transcribe <see cref="TranscriptionBufferDurationMinimum"/> than to record it, the duration of the next constructed wave file will increase to include all unprocessed samples.
/// </remarks>
public class WhisperContinuousBufferedInference : IModelInference<IFile, AudioFileInferenceSegment>, IFlushable
{
    private bool _justEmptied;
    private bool _isFlushing;
    private bool _speechStartInBuffer;
    private bool _speechEndInBuffer;
    private uint _speechEndInBufferCount;
    private DateTimeOffset? _thisBufferStart;
    private TimeSpan _bufferSilenceSkippedDurationSinceLastFlush = TimeSpan.Zero;
    private TimeSpan _bufferSilenceSkippedDurationSinceLastSpeechEnd = TimeSpan.Zero;
    private TimeSpan _bufferSilenceSkippedDurationSinceLastSpeechStart = TimeSpan.Zero;
    private MemoryStream? _transcriptionBufferStream;
    private SemaphoreSlim? _audioWriteMutex;

    /// <summary>
    /// The duration used to determine the buffers size when converting samples to a file and buffering from the underlying device or wave provider.
    /// </summary>
    public TimeSpan DeviceBufferDuration { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The minimum batch time for the transcription buffer.
    /// </summary>
    /// <remarks>
    /// If it takes longer to transcribe this duration than to record it, the duration of the next constructed wave file will increase to include all unprocessed samples.
    /// <para/>
    /// Use a high value (6-10) and raise <see cref="SpeechEndBufferDurationMultiple"/> when inference is fast (prevents flushing too early to maintain speed), or use a low value (1-3) and lower <see cref="SpeechEndBufferDurationMultiple"/> when inference is slow (accumulates larger buffers while emitting them before they're too large).  
    /// </remarks>
    public TimeSpan TranscriptionBufferDurationMinimum { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// The amount of time speech audio frames must present for voice detection to activate.
    /// </summary>
    public TimeSpan VadActivationTime { get; set; } = TimeSpan.FromMilliseconds(30);

    /// <summary>
    /// The amount of time speech audio frames must be absent for voice detection to deactivate. 
    /// </summary>
    /// <remarks>
    /// Decides the maximum amount of silence between pauses that makes it into the transcription buffer.
    /// <para/>
    /// Decides how often speech ends, and therefore decides how many times <see cref="SpeechEndBufferDurationMultiple"/> is used to extend <see cref="TranscriptionBufferDurationMinimum"/>.
    /// </remarks>
    public TimeSpan VadHangoverTime { get; set; } = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Adjust based on power of mic input gain.
    /// </summary>
    /// <remarks>
    /// Default value 0.0002f tested against simple usb desk mic at a range of gain levels from varying distances.
    /// Couldn't go below this without false positives on high-gain input. 
    /// </remarks>
    public float VadEnergyThreshold { get; set; } = 0.0002f;

    /// <summary>
    /// Gets or sets a value indicating whether auto-flushing should include both silence and speech, or only speech. 
    /// </summary>
    /// <remarks>
    /// Recommended when inference is fast or real-time, not recommended when inference is slow.
    /// </remarks>
    public bool SilenceAlsoFlushes { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating how much speech that starts again after ending before the silence-aware flush at <see cref="TranscriptionBufferDurationMinimum"/> increases a multiplier of the requested buffer duration. 
    /// </summary>
    /// <remarks>
    /// Defaults to 0.25, adds exactly 0.25x the <see cref="TranscriptionBufferDurationMinimum"/> to the inference duration threshold each time speech ends. 
    /// <para/>
    /// A number between 0 and 1 guarantee that the buffer duration cannot be extended indefinitely even if the user never stops starting/stopping talking,
    /// while a number greater than 1 may extend the buffer duration indefinitely if the user never stops starting/stopping talking. 
    /// </remarks>
    public double SpeechEndBufferDurationMultiple { get; set; } = 0.25;

    /// <summary>
    /// The inference used to process the constructed wave file of at least <see cref="TranscriptionBufferDurationMinimum"/> duration for inference.
    /// </summary>
    public required IModelInference<IFile, SegmentData> FileInference { get; init; }

    /// <summary>
    /// Raised when samples are read from the live input wave file. 
    /// </summary>
    public event EventHandler<byte[]>? SamplesReadBytes;

    /// <summary>
    /// Raised when samples are read from the live input wave file. 
    /// </summary>
    public event EventHandler<float[]>? SamplesReadFloats;

    /// <summary>
    /// Infers audio from a live <paramref name="liveWavFile"/> stream continuously until cancellation.
    /// </summary>
    /// <param name="liveWavFile">The continuous wave provider to process.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns></returns>
    public async IAsyncEnumerable<AudioFileInferenceSegment> InferAsync(IFile liveWavFile, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var input = (IWaveProvider)await liveWavFile.OpenReadAsync(cancellationToken);

        // Calculate-once buffer size variables
        var bytesPerSample = input.WaveFormat.BitsPerSample / 8;
        var bytesPerFrame = bytesPerSample * input.WaveFormat.Channels;

        var deviceBufferSize = input.WaveFormat.ConvertLatencyToByteSize(milliseconds: (int)DeviceBufferDuration.TotalMilliseconds);
        Guard.IsNotEqualTo(0, deviceBufferSize);

        var transcriptionBufferSize = input.WaveFormat.ConvertLatencyToByteSize(milliseconds: (int)TranscriptionBufferDurationMinimum.TotalMilliseconds);
        Guard.IsNotEqualTo(0, transcriptionBufferSize);

        // Buffer sink to collect audio samples
        // from the underlying device for inference.
        _transcriptionBufferStream ??= new MemoryStream();

        // Keep inference start in sync with start/end of background audio capture
        _audioWriteMutex ??= new SemaphoreSlim(1, 1);

        // Voice detection:
        // - Provides bool IsVoiceActive (computed per processed frames, not wall clock)
        // - Provides event SpeechDetected (raised when IsVoiceActive set to true)
        var voiceActivityDetector = new VoiceActivityDetector(new()
        {
            Format = input.WaveFormat.BitsPerSample switch
            {
                8 => SampleFormat.U8,
                16 => SampleFormat.S16,
                24 => SampleFormat.S24,
                32 => SampleFormat.S32,
                _ => SampleFormat.Unknown,
            },
            Channels = input.WaveFormat.Channels,
            Layout = input.WaveFormat.Channels switch
            {
                1 => ChannelLayout.Mono,
                2 => ChannelLayout.Stereo,
                _ => ChannelLayout.Unknown
            },
            SampleRate = input.WaveFormat.SampleRate,
        }, fftSize: 512, energyThreshold: VadEnergyThreshold)
        {
            ActivationTimeMs = (float)VadActivationTime.TotalMilliseconds,
            HangoverTimeMs = (float)VadHangoverTime.TotalMilliseconds,
            SpeechLowBand = 85,
            SpeechHighBand = 4000,
        };

        voiceActivityDetector.SpeechDetected += isDetected => Logger.LogInformation($"Speech detected: {isDetected}");

        // Read from underlying device in background
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Use mutex to avoid writing until read operations are done
                using (await _audioWriteMutex.DisposableWaitAsync(cancellationToken))
                {
                    var isFirstBuffer = _thisBufferStart is null;
                    if (isFirstBuffer || _justEmptied)
                    {
                        _thisBufferStart = DateTimeOffset.Now;
                    }

                    // Acknowledge emptied signal (start of next buffer chunk)
                    if (_justEmptied)
                        _justEmptied = false;

                    // Read from capture input stream
                    var audioDeviceBuffer = new byte[deviceBufferSize];
                    var bytesRead = input.Read(audioDeviceBuffer, 0, audioDeviceBuffer.Length);
                    if (bytesRead == 0)
                    {
                        await Task.Delay(10, cancellationToken);
                        continue;
                    }

                    // Trim requested buffer to bytes that were read.
                    var samplesF = new float[bytesRead];
                    var samplesB = new byte[bytesRead];
                    for (var i = 0; i < bytesRead; i++)
                    {
                        // Float for VAD
                        samplesF[i] = (audioDeviceBuffer[i] - 128f) / 128f;

                        // Byte for transcription buffer stream
                        samplesB[i] = audioDeviceBuffer[i];
                    }

                    SamplesReadBytes?.Invoke(this, samplesB);
                    SamplesReadFloats?.Invoke(this, samplesF);

                    // Detect voice activity
                    voiceActivityDetector.Process(samplesF, input.WaveFormat.Channels);

                    // Queue buffer for next inference
                    if (voiceActivityDetector.IsVoiceActive)
                    {
                        _speechStartInBuffer = true;
                        _speechEndInBuffer = false;

                        _transcriptionBufferStream.Write(samplesB, 0, bytesRead);
                    }
                    else
                    {
                        if (_speechStartInBuffer)
                        {
                            // Mark silence as the end of speech if saw speech start
                            if (!_speechEndInBuffer)
                            {
                                _speechEndInBuffer = true;
                                _speechEndInBufferCount++;
                            }
                        }
                        else
                        {
                            // If there's no start but also silence in the buffer, it's not the end. 
                            _speechEndInBuffer = false;
                            _bufferSilenceSkippedDurationSinceLastSpeechEnd = TimeSpan.Zero;
                        }

                        // Drop buffers with silence, note skipped duration for next inference/flush. 
                        var sampleCount = bytesRead / bytesPerFrame;
                        var bufferDurationSeconds = (double)sampleCount / input.WaveFormat.SampleRate;
                        Guard.IsNotEqualTo(0, bufferDurationSeconds);

                        if (_speechStartInBuffer && !_speechEndInBuffer)
                            _bufferSilenceSkippedDurationSinceLastSpeechStart += TimeSpan.FromSeconds(bufferDurationSeconds);
                            
                        if (_speechEndInBuffer && _speechStartInBuffer)
                            _bufferSilenceSkippedDurationSinceLastSpeechEnd += TimeSpan.FromSeconds(bufferDurationSeconds);

                        // Only relative to speech end in last buffer, not this buffer. 
                        _bufferSilenceSkippedDurationSinceLastFlush += TimeSpan.FromSeconds(bufferDurationSeconds);
                    }
                }
            }
        }, cancellationToken);

        // Get time since live transcription file was created (when recording actually started)
        DateTimeOffset? liveCaptureStartedAt = null;

        // Each loop iteration dumps and infers the accumulated sample buffer
        while (!cancellationToken.IsCancellationRequested)
        {
            // Flush (or not)
            // If buffer full or flush is requested, process buffer
            var sampleCount = _transcriptionBufferStream.Length / bytesPerFrame;
            var bufferDurationSeconds = (double)sampleCount / input.WaveFormat.SampleRate;
            var bufferDuration = TimeSpan.FromSeconds(bufferDurationSeconds);
            var bufferSilenceSkippedDurationSinceSpeechEnd = _bufferSilenceSkippedDurationSinceLastSpeechEnd;
            var bufferSilenceSkippedDurationSinceLastSpeechStart = _bufferSilenceSkippedDurationSinceLastSpeechStart;

            Guard.IsGreaterThanOrEqualTo(SpeechEndBufferDurationMultiple, 0);
            var transcriptionBufferDurationMinimum = TranscriptionBufferDurationMinimum;

            // Check for buffer extension
            // multiple = always at least one
            // speech end = may be zero
            // 
            // adds buffer duration again when speech ends, times multiple (1, 2, etc.)
            // adds zero when no speed end
            // 
            // multiple does not increase, repeats via speech end instead
            // not a power or base, proper multiple that extends the track either 1x, 2x, 3x, etc., each time speech is ended.
            var transcriptionBufferDurationMinimumExtension = transcriptionBufferDurationMinimum * _speechEndInBufferCount * SpeechEndBufferDurationMultiple;
            var extendedTranscriptionBufferDurationMinimum = transcriptionBufferDurationMinimum + transcriptionBufferDurationMinimumExtension;

            var speechFillsOrExceedsBuffer = _transcriptionBufferStream.Length >= transcriptionBufferSize;
            var speechWithSilenceExceedsBuffer = (bufferDuration + bufferSilenceSkippedDurationSinceSpeechEnd) > extendedTranscriptionBufferDurationMinimum;

            // User must be done talking to flush. 
            // Either the speech buffer is fully filled, or it's partially filled and ignored silence would have filled the rest.
            var finishedSpeechInFullBuffer = speechFillsOrExceedsBuffer;
            var finishedSpeechSilencePaddedBuffer = speechWithSilenceExceedsBuffer && _speechEndInBufferCount > 0 && SilenceAlsoFlushes;
            var bufferFull = finishedSpeechInFullBuffer || finishedSpeechSilencePaddedBuffer;

            var shouldFlush = bufferFull && !voiceActivityDetector.IsVoiceActive;
            if (shouldFlush || _isFlushing)
            {
                _isFlushing = true;
                MemoryStream samplesBuffer = _transcriptionBufferStream;

                // Collect buffers and zero out the stream
                // Use mutex to prevent writing while we read
                DateTimeOffset? thisBufferStart;
                using (await _audioWriteMutex.DisposableWaitAsync(cancellationToken))
                {
                    // Since we have mutex lock
                    // Instead of moving bytes, trade references                    
                    // Replace device sink with fresh stream
                    _transcriptionBufferStream = new MemoryStream();

                    // Capture original buffer start while field in mutex
                    thisBufferStart = _thisBufferStart;

                    // Signal start of next buffer chunk boundary
                    _justEmptied = true;
                    _bufferSilenceSkippedDurationSinceLastFlush = TimeSpan.Zero;
                    _bufferSilenceSkippedDurationSinceLastSpeechStart = TimeSpan.Zero;
                    _speechStartInBuffer = false;
                    _speechEndInBuffer = false;
                    _speechEndInBufferCount = 0;
                }

                // Only proceed with non-empty buffer
                Guard.IsNotNull(samplesBuffer);
                Guard.IsNotEqualTo(0, samplesBuffer.Length);

                // Create a wav file from stream
                var sampleBatchFile = await CreateWavFileWithHeadersAsync(samplesBuffer, input.WaveFormat, cancellationToken);
                Logger.LogInformation($"Inferring file {sampleBatchFile.Id} constructed from {samplesBuffer.Length} length.");

                // Get time since file inference started (relative to when continuous inference started)
                // TODO: do we still need this? 
                DateTimeOffset sampleBatchEndedAt = await GetCreatedAtOffsetAsync(sampleBatchFile, cancellationToken);
                liveCaptureStartedAt ??= await GetCreatedAtOffsetAsync(liveWavFile, cancellationToken);

                // Infer sample batch file
                var sampleBatchStartedAt = thisBufferStart ?? liveCaptureStartedAt;
                TimeSpan timeToSampleBatchStartSinceLiveStart = sampleBatchStartedAt.Value - liveCaptureStartedAt.Value;
                await foreach (var segment in FileInference.InferAsync(sampleBatchFile, cancellationToken))
                {
                    var segmentCopy = new SegmentData(
                        text: segment.Text,
                        start: timeToSampleBatchStartSinceLiveStart + bufferSilenceSkippedDurationSinceSpeechEnd - bufferSilenceSkippedDurationSinceLastSpeechStart + segment.Start,
                        end: timeToSampleBatchStartSinceLiveStart + bufferSilenceSkippedDurationSinceSpeechEnd - bufferSilenceSkippedDurationSinceLastSpeechStart + segment.End,
                        minProbability: segment.MinProbability,
                        maxProbability: segment.MaxProbability,
                        probability: segment.Probability,
                        noSpeechProbability: segment.NoSpeechProbability,
                        language: segment.Language,
                        tokens: segment.Tokens
                    );

                    yield return new(segmentCopy, sampleBatchFile);
                }

                // Signal finished buffer flush
                _isFlushing = false;
            }
        }

        if (input is IDisposable disposable)
            disposable.Dispose();
    }

    private static async Task<DateTimeOffset> GetCreatedAtOffsetAsync(IFile file, CancellationToken cancellationToken)
    {
        DateTimeOffset? createdAt;
        if (file.CreatedAtOffset is { } liveCreatedAtOffset && await liveCreatedAtOffset.GetValueAsync(cancellationToken) is { } createdAtOffsetValue)
        {
            createdAt = createdAtOffsetValue;
        }
        else if (file.CreatedAt is { } liveCreatedAt && await liveCreatedAt.GetValueAsync(cancellationToken) is { } createdAtValue)
        {
            createdAt = createdAtValue;
        }
        else
        {
            throw new NotSupportedException();
        }

        Guard.IsNotNull(createdAt);
        return createdAt.Value;
    }

    /// <summary>
    /// Flushes any buffered data to the underlying storage or output, bypassing <see cref="TranscriptionBufferDurationMinimum"/> for any pending calls to <see cref="InferAsync(IFile, CancellationToken)"/>.
    /// </summary>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        // Flush needs to reach the internal loops of InferAsync, so we use a sentinel field.
        // Begin flush if not already started
        if (!_isFlushing)
            _isFlushing = true;

        // Wait for flush to complete
        while (_isFlushing)
            await Task.Delay(100, cancellationToken);
    }

    async Task<IFile> CreateWavFileWithHeadersAsync(MemoryStream audioBufferWithoutHeaders, WaveFormat waveFormat, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var wavHeadersStream = new MemoryStream();
        var wavFile = new MemoryFile(id: $"{DateTime.UtcNow.ToFileTimeUtc()}", name: $"{DateTime.UtcNow.ToFileTimeUtc()}.wav", wavHeadersStream);

        var transcriptionBufferWaveWriter = new WaveFileWriter(new NonDisposableStreamWrapper(wavHeadersStream), waveFormat);
        audioBufferWithoutHeaders.Seek(0, SeekOrigin.Begin);
        audioBufferWithoutHeaders.CopyTo(transcriptionBufferWaveWriter);

        await transcriptionBufferWaveWriter.DisposeAsync();
        wavHeadersStream.Seek(0, SeekOrigin.Begin);

        return wavFile;
    }
}