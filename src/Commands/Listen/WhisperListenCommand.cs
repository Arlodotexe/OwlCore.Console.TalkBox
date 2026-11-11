using System.CommandLine;
using System.Globalization;
using OwlCore.Console.TalkBox.Whisper;
using OwlCore.Diagnostics;
using OwlCore.Storage;
using OwlCore.Storage.OpenAL;
using OwlCore.Storage.System.IO;
using Silk.NET.OpenAL;
using Whisper.net;
using Whisper.net.Ggml;

namespace OwlCore.Console.TalkBox.Commands.Whisper;

public class WhisperListenCommand : Command
{
    private readonly CancellationToken _cancellationToken;

    /// <summary>
    /// Creates a new instance of <see cref="WhisperListenCommand"/>.
    /// </summary>
    /// <param name="cancellationToken"></param>
    public WhisperListenCommand(CancellationToken cancellationToken)
        : base("listen", "Listen and transcribe audio from an audio capture device continuously.")
    {
        _cancellationToken = cancellationToken;

        var inputDeviceOption = new Option<string?>("--inputDevice")
        {
            DefaultValueFactory = _ => null,
            Description = "The name of the audio input (capture) device to listen through. Leave out to list available devices; pass 'default' for the default device."
        };

        var whisperRepoOption = new Option<string?>("--whisperRepo")
        {
            DefaultValueFactory = _ => null,
            Description = "The path where an offline whisper repository (models) is saved."
        };

        var modelTypeOption = new Option<GgmlType>("--modelType")
        {
            DefaultValueFactory = _ => GgmlType.LargeV3Turbo,
            Description = "The whisper model type to use."
        };

        var quantizationTypeOption = new Option<QuantizationType>("--quantizationType")
        {
            DefaultValueFactory = _ => QuantizationType.Q4_0,
            Description = "The whisper model quantization type to use."
        };

        this.SetAction(x => ListenAsync(
            x.GetValue(inputDeviceOption),
            x.GetValue(whisperRepoOption),
            x.GetValue(modelTypeOption),
            x.GetValue(quantizationTypeOption)));

        this.Options.Add(inputDeviceOption);
        this.Options.Add(whisperRepoOption);
        this.Options.Add(modelTypeOption);
        this.Options.Add(quantizationTypeOption);
    }
    public async Task ListenAsync(string? inputDevice, string? whisperRepo, GgmlType modelType, QuantizationType quantizationType)
    {
        // Leaving a few notes here:
        // - Everything is a file or folder, and a stream can be of a predetermined size (like a stored file on disk), or can be a continuous stream (like an audio device or a socket).
        // - With nomad, changes to a file or folder are kept in a timeline.
        // - Creating or updating files of a predetermined size to nomad.storage.kubo is straightforward: the complete set of data is hashed into a CID. 
        // - Creating or updating files with a continuous stream is not so straightforward, as the stream has no end position and is never "complete".
        // 
        // Here's what we can do:
        // - For the incoming continuous stream, buffers are written as updates to the same file.
        // - Then, the complete history of updates (every buffer written) will be available as Nomad data.
        // - Now, aggregate the buffers for a given time period (adding headers where needed) and return the data via a Stream.
        // - This allows us to interpret a continuous stream (like wav samples from a device) like a normal file with a set length (such as a .wav) by limiting Nomad data to a time range.

        // Actual code
        var cancellationToken = _cancellationToken;

        // Select audio capture (mic) source
        var captureDevices = new OpenALCaptureDevicesFolder();

        // No device specified: list available device names instead of defaulting.
        if (string.IsNullOrWhiteSpace(inputDevice))
        {
            System.Console.WriteLine("Available audio input devices:");
            await foreach (var child in captureDevices.GetItemsAsync(StorableType.All, cancellationToken))
            {
                var device = (OpenALCaptureDeviceFile)child;
                System.Console.WriteLine(device.Name);
            }
            return;
        }

        // Match by device name (case-insensitive). The 'default' keyword selects the default
        // item by Id because its Name is the default device's own specifier name, not "default".
        var deviceName = inputDevice.Trim();
        var deviceChildren = new List<OpenALCaptureDeviceFile>();
        await foreach (var child in captureDevices.GetItemsAsync(StorableType.All, cancellationToken))
            deviceChildren.Add((OpenALCaptureDeviceFile)child);

        var isDefault = deviceName.Equals("default", StringComparison.OrdinalIgnoreCase);
        var captureDevice = deviceChildren.FirstOrDefault(d =>
            d.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
            (isDefault && d.Id.Equals("default", StringComparison.Ordinal)));
        if (captureDevice is null)
        {
            // Unknown device: print the available names instead of crashing on an invalid open.
            System.Console.WriteLine($"Unknown audio input device '{deviceName}'.");
            System.Console.WriteLine("Available audio input devices:");
            foreach (var d in deviceChildren)
                System.Console.WriteLine(d.Name);
            return;
        }

        captureDevice.Frequency = 16000;
        captureDevice.Format = BufferFormat.Mono8;

        // Offline whisper repository (models) — mirrors the piper repository pattern from the say command.
        // Point at a folder to persist downloaded models; the same folder then works on a machine
        // with no internet. Defaults to a .whisper folder under the system temp path.
        var whisperRepoFolder = whisperRepo is not null ?
            new SystemFolder(Path.GetFullPath(whisperRepo)) :
            (SystemFolder)await new SystemFolder(Path.GetTempPath()).CreateFolderAsync(".whisper", overwrite: false, cancellationToken);
        var whisperModelFolder = (SystemFolder)await whisperRepoFolder.CreateFolderAsync("models", overwrite: false, cancellationToken);

        // Download (or open, if already present in the repository) and set up the whisper model
        var whisperModelFile = await whisperModelFolder.DownloadOrOpenWhisperModelAsync(modelType, quantizationType, overwrite: false, cancellationToken: cancellationToken);

        // Large model can't be copied to a byte[] buffer, use local file instead.
        // Copy to temp location on disk if needed.
        if (whisperModelFile is not SystemFile)
        {
            var tempFolder = new SystemFolder(Path.GetTempPath());
            whisperModelFile = (SystemFile)await tempFolder.CreateCopyOfAsync(whisperModelFile, overwrite: true, cancellationToken);
        }

        var onDiskWhisperModelFile = (SystemFile)whisperModelFile;
        var whisperProcessorBuilder = WhisperFactory.FromPath(onDiskWhisperModelFile.Path).CreateBuilder();
        var whisperProcessor = whisperProcessorBuilder
            .WithProbabilities()
            .WithEntropyThreshold(2.8f)
            .WithThreads(8)
            .WithNoSpeechThreshold(0.6f)
            .WithLengthPenalty(0.7f)
            .WithLogProbThreshold(-1.0f)
            .WithTemperature(0.2f)
            .WithTemperatureInc(0.1f)
            .Build();

        // TODO: Inference delegation (p2p)
        var inference = new WhisperContinuousBufferedInference
        {
            FileInference = new WhisperInference
            {
                DeviceBufferDuration = TimeSpan.FromMilliseconds(100),
                WhisperProcessor = whisperProcessor,
            },
            DeviceBufferDuration = TimeSpan.FromMilliseconds(100),
            TranscriptionBufferDurationMinimum = TimeSpan.FromSeconds(3),
            SilenceAlsoFlushes = true,
            VadActivationTime = TimeSpan.FromMilliseconds(30),
            VadHangoverTime = TimeSpan.FromMilliseconds(1000),
            SpeechEndBufferDurationMultiple = 2,
        };

        await foreach (var segment in inference.InferAsync(captureDevice, cancellationToken))
        {
            var actualStart = captureDevice.CaptureStream?.CaptureStartedAt + segment.Data.Start;
            var actualEnd = captureDevice.CaptureStream?.CaptureStartedAt + segment.Data.End;

            // Intentionally use stdout instead of diagnostic logger
            System.Console.WriteLine($"{actualStart:G} to {actualEnd:G}: {segment.Data.Text}");
        }
    }
}