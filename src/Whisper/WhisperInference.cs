using System.Runtime.CompilerServices;
using OwlCore.AI.Inference;
using OwlCore.Diagnostics;
using OwlCore.Storage;
using Whisper.net;

namespace OwlCore.Console.TalkBox.Whisper;

/// <summary>
/// Handles Whisper inference for files and wav providers.
/// </summary>
public class WhisperInference : IModelInference<IFile, SegmentData>
{
    /// <summary>
    /// The duration used to determine the buffers size when converting samples to a file and buffering from the underlying device or wave provider.
    /// </summary>
    public TimeSpan DeviceBufferDuration { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The processor to use for Whisper inference.
    /// </summary>
    public required WhisperProcessor WhisperProcessor { get; set; }

    /// <summary>
    /// Infers the input wave file using Whisper, transforming into audio segments.
    /// </summary>
    /// <param name="input">The wave provider to process.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing task.</param>
    public async virtual IAsyncEnumerable<SegmentData> InferAsync(IFile input, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var wavStream = await input.OpenReadAsync(cancellationToken);

        List<SegmentData> items = null!;
        try
        {
            items = await WhisperProcessor.ProcessAsync(wavStream, cancellationToken)
                .ToListAsync(cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError("err", ex);
        }

        foreach (var segment in items)
        {
            yield return segment;
        }
    }
}