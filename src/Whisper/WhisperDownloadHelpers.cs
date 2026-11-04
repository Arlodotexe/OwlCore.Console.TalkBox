using OwlCore.Storage;
using Whisper.net;
using Whisper.net.Ggml;

namespace OwlCore.Console.TalkBox.Whisper;

/// <summary>
/// Helpers for download whisper models to any OwlCore.Storage implementation.
/// </summary>
public static class WhisperDownloadHelpers
{
    /// <summary>
    /// Downloads a specific whisper model to the specified <paramref name="destinationFile"/>.
    /// </summary>
    /// <param name="destinationFile">The destination file to write to.</param>
    /// <param name="modelType">The model type to download.</param>
    /// <param name="quantizationType">The quantization type to download.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public static async Task DownloadWhisperModelAsync(this IFile destinationFile, GgmlType modelType, QuantizationType quantizationType, CancellationToken cancellationToken)
    {
        var whisperGgmlModelFile = destinationFile;
        using var ggmlModelStream = await whisperGgmlModelFile.OpenWriteAsync(cancellationToken);

        var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(modelType, quantizationType, cancellationToken: cancellationToken);
        await modelStream.CopyToAsync(ggmlModelStream, 81920, cancellationToken);
    }

    /// <summary>
    /// Downloads or opens a whisper model in the provided folder.
    /// </summary>
    /// <param name="folder">The folder to download or open a whisper model.</param>
    /// <param name="modelType">The model type for whisper.</param>
    /// <param name="quantizationType">The quantization type for whisper. <seealso cref="QuantizationType"/>.</param>
    /// <param name="overwrite">Whether to overwrite any existing file with the same name as the requested model type.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    /// <returns>An instance of <see cref="WhisperProcessorBuilder"/> that can be used to configure and build an instance of <see cref="WhisperProcessor"/>.</returns>
    public static async Task<IFile> DownloadOrOpenWhisperModelAsync(this IModifiableFolder folder, GgmlType modelType, QuantizationType quantizationType, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        // Load model
        var whisperGgmlModelFile = await folder.CreateFileAsync($"whisper.{modelType}.{quantizationType}.ggml", overwrite: overwrite, cancellationToken);
        var ggmlModelStream = await whisperGgmlModelFile.OpenReadAsync(cancellationToken);

        // Download if needed
        if (ggmlModelStream.Length == 0)
        {
            await ggmlModelStream.DisposeAsync();
            await whisperGgmlModelFile.DownloadWhisperModelAsync(modelType, quantizationType, cancellationToken);
        }

        await ggmlModelStream.DisposeAsync();
        return whisperGgmlModelFile;
    }
}