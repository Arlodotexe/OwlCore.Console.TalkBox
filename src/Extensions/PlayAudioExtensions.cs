using NAudio.Wave;
using OwlCore.Diagnostics;
using OwlCore.Extensions;
using OwlCore.Storage;
using OwlCore.Storage.OpenAL;
using Silk.NET.OpenAL;

namespace OwlCore.Console.Storage.Extensions;

/// <summary>
/// Extension methods and helpers for storage.
/// </summary>
public static partial class StorageExtensions
{
    /// <summary>
    /// Plays a WAV file through the default OpenAL playback device (speakers).
    /// </summary>
    /// <param name="file">The WAV file to play.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public static Task PlayWavAsync(this IFile file, CancellationToken cancellationToken = default) => PlayWavAsync(file, null, cancellationToken);

    /// <summary>
    /// Plays a WAV file through the named OpenAL playback device.
    /// </summary>
    /// <param name="file">The WAV file to play.</param>
    /// <param name="outputDevice">The OpenAL device name to route output to. Leave out to list available devices instead of playing; pass 'default' for the default device.</param>
    /// <param name="cancellationToken">A token that can be used to cancel the ongoing operation.</param>
    public static async Task PlayWavAsync(this IFile file, string? outputDevice, CancellationToken cancellationToken = default)
    {
        // Audio playback devices (named, or the default device when "default" is passed explicitly)
        var playbackDevices = new OpenALDevicesFolder();

        // No device specified: list available device names instead of defaulting.
        if (string.IsNullOrWhiteSpace(outputDevice))
        {
            System.Console.WriteLine("Available audio output devices:");
            await foreach (var child in playbackDevices.GetItemsAsync(StorableType.All, cancellationToken))
            {
                var device = (OpenALDeviceFile)child;
                System.Console.WriteLine(device.Name);
            }
            return;
        }

        // Open file
        using var fileStream = await file.OpenReadAsync(cancellationToken);
        using var waveFileReader = new WaveFileReader(fileStream);

        // Match by device name (case-insensitive). The 'default' keyword selects the default
        // item by Id because its Name is the default device's own specifier name, not "default".
        var deviceName = outputDevice.Trim();
        var deviceChildren = new List<OpenALDeviceFile>();
        await foreach (var child in playbackDevices.GetItemsAsync(StorableType.All, cancellationToken))
            deviceChildren.Add((OpenALDeviceFile)child);

        var isDefault = deviceName.Equals("default", StringComparison.OrdinalIgnoreCase);
        var playbackDevice = deviceChildren.FirstOrDefault(d =>
            d.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
            (isDefault && d.Id.Equals("default", StringComparison.Ordinal)));
        if (playbackDevice is null)
        {
            // Unknown device: print the available names instead of throwing on an invalid open.
            System.Console.WriteLine($"Unknown audio output device '{deviceName}'.");
            System.Console.WriteLine("Available audio output devices:");
            foreach (var d in deviceChildren)
                System.Console.WriteLine(d.Name);
            return;
        }
        await using var devicePlaybackStream = (OpenALDeviceStream)await playbackDevice.OpenWriteAsync(cancellationToken);

        devicePlaybackStream.Frequency = waveFileReader.WaveFormat.SampleRate;
        devicePlaybackStream.BufferSize = devicePlaybackStream.Frequency * waveFileReader.WaveFormat.Channels * (waveFileReader.WaveFormat.BitsPerSample / 8) / 10;
        Logger.LogInformation($"BufferSize: {devicePlaybackStream.BufferSize} bytes");
        devicePlaybackStream.BufferPlaybackThreshold = devicePlaybackStream.BufferSize * 2;
        devicePlaybackStream.BufferFormat = waveFileReader.WaveFormat.Channels switch
        {
            1 => BufferFormat.Mono16,
            2 => BufferFormat.Stereo16,
            _ => throw new NotSupportedException($"Unsupported channel count: {waveFileReader.WaveFormat.Channels}")
        };

        // Check header data
        Logger.LogInformation($"WaveFormat: {waveFileReader.WaveFormat}");
        Logger.LogInformation($"Length: {waveFileReader.Length}");

        var readBufferFrameSize = waveFileReader.WaveFormat.BitsPerSample / 8 * waveFileReader.WaveFormat.Channels;
        var readBufferFrame = new byte[readBufferFrameSize];
        Logger.LogInformation($"readBufferFrameSize: {readBufferFrameSize} bytes");

        // Play wav file
        while (waveFileReader.Position < waveFileReader.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = waveFileReader.Read(readBufferFrame, 0, readBufferFrameSize);
            if (bytesRead == 0)
                break;

            devicePlaybackStream.Write(readBufferFrame, 0, bytesRead);
        }

        // Flush the playback stream to ensure all data is played
        await devicePlaybackStream.FlushAsync(cancellationToken);
    }
}
