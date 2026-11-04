using System.CommandLine;
using NAudio.Wave;
using OwlCore.Diagnostics;
using OwlCore.Storage;
using OwlCore.Storage.Memory;
using OwlCore.Storage.OpenAL;
using OwlCore.Storage.System.IO;
using PiperSharp;
using PiperSharp.Models;
using CommunityToolkit.Diagnostics;
using System.Runtime.CompilerServices;
using Silk.NET.OpenAL;
using OwlCore.Console.TalkBox.Settings;

namespace OwlCore.Console.TalkBox.Commands.Say;

public class SayCommand : Command
{
    private readonly CancellationToken cancellationToken;

    /// <summary>
    /// Creates a new instance of <see cref="SayCommand"/>.
    /// </summary>
    /// <param name="cancellationToken"></param>
    public SayCommand(CancellationToken cancellationToken)
        : base("say", "Use piper for text to speech.")
    {
        this.cancellationToken = cancellationToken;

        var textOption = new Option<string?>("--text")
        {
            DefaultValueFactory = _ => null
        };

        var textFilePathOption = new Option<string?>("--file")
        {
            DefaultValueFactory = _ => null,
            Description = "The path to a text file to synthesize."
        };

        var voiceModelOption = new Option<string?>("--voiceModelKey")
        {
            DefaultValueFactory = _ => null,
            Description = "The voice model to use by key. Exclude to list available options."
        };

        var piperRepoOption = new Option<string?>("--piperRepo")
        {
            DefaultValueFactory = _ => null,
            Description = "The path where an offline piper repository (binaries, models, settings) is saved."
        };

        var outputDeviceOption = new Option<string?>("--outputDevice")
        {
            DefaultValueFactory = _ => null,
            Description = "The name of the audio output device to play through. Leave out to list available devices; pass 'default' for the default device."
        };

        this.SetAction(x => SayAsync(x.GetValue(textOption), x.GetValue(textFilePathOption), x.GetValue(voiceModelOption), x.GetValue(piperRepoOption), x.GetValue(outputDeviceOption)));
        this.Options.Add(textOption);
        this.Options.Add(textFilePathOption);
        this.Options.Add(voiceModelOption);
        this.Options.Add(piperRepoOption);
        this.Options.Add(outputDeviceOption);
    }


    public async Task SayAsync(string? text, string? textFilePath, string? voiceModelKey, string? piperRepo, string? outputDevice)
    {
        // Read text from path if given
        if (textFilePath is not null)
        {
            var pathSplit = textFilePath.Split('#');
            var textRange = pathSplit.ElementAtOrDefault(1);
            var file = new SystemFile(pathSplit[0]);
            if (textRange is not null)
            {
                var (lr, cr) = PathRangeHelpers.GetRanges(textRange);
                Logger.LogInformation($"Using line ranges start {lr.Value.Start.Value} end {lr.Value.End.Value}");

                if (lr is not null && cr is not null)
                    text = await file.ReadTextAsync(lr.Value, cr.Value, cancellationToken).AggregateAsync((x, y) => x + y);
                else if (lr is not null && cr is null)
                    text = await file.ReadTextAsync(lr.Value, cancellationToken).AggregateAsync((x, y) => x + y);
                else
                    throw new InvalidOperationException("Unhandled line/column range code path.");
            }
            else
                text = await file.ReadTextAsync();
        }

        Guard.IsNotNull(text);

        // Remove markdown syntax
        text = text
            .Replace("*", "")
            .Replace("_", "")
            .Replace("#", "");

        var piperRepoFolder = piperRepo is not null ?
            new SystemFolder(Path.GetFullPath(piperRepo)) :
            (SystemFolder)await new SystemFolder(Path.GetTempPath()).CreateFolderAsync(".piper", overwrite: false, cancellationToken);

        PiperProvider? piperModel = await GetPiperAsync(piperRepoFolder, voiceModelKey, cancellationToken);

        if (piperModel is null)
            return;

        // Generate audio, currently supported formats are Mp3, Wav, Raw
        Logger.LogInformation($"Generating audio for text: {text}");
        byte[]? result = await piperModel.InferAsync(text, AudioOutputType.Wav, cancellationToken);
        if (result is null)
            return;

        Logger.LogInformation($"Writing audio {result.Length} bytes to memory file for playback");
        var audioFile = new MemoryFile(new()); // Start with empty file
        await audioFile.WriteBytesAsync(result, cancellationToken);

        Logger.LogInformation($"Audio generation complete ({result.Length} bytes). Playing audio...");
        await PlayAudioAsync(audioFile, outputDevice, cancellationToken);
    }

    private async IAsyncEnumerable<string> SpeakTextRangeAsync(string text, Range lineRange, Range? columnRange, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }

    private async Task<PiperProvider?> GetPiperAsync(SystemFolder piperFolder, string? voiceModelKey, CancellationToken cancellationToken = default)
    {
        var modelFolder = (SystemFolder)await piperFolder.CreateFolderAsync("models", cancellationToken: cancellationToken);
        SystemFolder? downloadedPiperPlatformBinariesFolder;
        SystemFile? downloadedPiperExecutable;

        var piperBinariesPlatformFolderName = Environment.OSVersion.Platform switch
        {
            PlatformID.Unix => "Linux",
            PlatformID.Win32NT => "Windows",
            _ => throw new NotImplementedException(),
        };

        var piperExecutableName = Environment.OSVersion.Platform switch
        {
            PlatformID.Unix => "piper",
            PlatformID.Win32NT => "piper.exe",
            _ => throw new NotImplementedException(),
        };

        downloadedPiperPlatformBinariesFolder = (SystemFolder)await piperFolder.CreateFolderAsync("binaries", overwrite: false, cancellationToken);
        downloadedPiperPlatformBinariesFolder = (SystemFolder)await downloadedPiperPlatformBinariesFolder.CreateFolderAsync(piperBinariesPlatformFolderName, overwrite: false, cancellationToken);

        // To download piper executable use
        try
        {
            var defaultPiperDownloadFolder = (SystemFolder)await downloadedPiperPlatformBinariesFolder.GetFirstByNameAsync("piper", cancellationToken);
            downloadedPiperExecutable = (SystemFile)await defaultPiperDownloadFolder.GetFirstByNameAsync(piperExecutableName, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            Logger.LogInformation("Piper executable not found, downloading...");
            try
            {
                await PiperDownloader.DownloadPiper().ExtractPiper(downloadedPiperPlatformBinariesFolder.Path);
            }
            catch (System.IO.IOException)
            {
                // Extraction partially completed in a prior run; files already present
                Logger.LogInformation("Piper binaries already present, skipping re-extraction.");
            }

            var defaultPiperDownloadFolder = (SystemFolder)await downloadedPiperPlatformBinariesFolder.GetFirstByNameAsync("piper", cancellationToken);
            downloadedPiperExecutable = (SystemFile)await defaultPiperDownloadFolder.GetFirstByNameAsync(piperExecutableName, cancellationToken);
        }

        ProcessHelpers.EnableExecutablePermissions(downloadedPiperExecutable.Path);

        // Load cached models
        var piperSettingsFolder = (SystemFolder)await piperFolder.CreateFolderAsync("settings", overwrite: false, cancellationToken);
        var piperSettings = new PiperSettings(piperSettingsFolder);
        await piperSettings.LoadAsync(cancellationToken);

        var loadedWithNoCachedVoiceModels = piperSettings.VoiceModels.Count == 0;
        if (loadedWithNoCachedVoiceModels)
        {
            // You can get list of models from hugging face using GetHuggingFaceModelList
            // Returns a dictionary with model key as key
            var models = await PiperDownloader.GetHuggingFaceModelList();
            Guard.IsNotNull(models);
            Guard.IsNotEqualTo(models.Count, 0);

            piperSettings.VoiceModels = models.Select(x => x.Value).ToList();
            await piperSettings.SaveAsync(cancellationToken);
        }

        // Print models
        if (string.IsNullOrWhiteSpace(voiceModelKey))
        {
            foreach (var cachedModel in piperSettings.VoiceModels)
            {
                Logger.LogInformation($"Model Key: {cachedModel.Key}, Model Name: {cachedModel.Name}");
            }
            return null;
        }

        var model = piperSettings.VoiceModels.First(x => x.Key == voiceModelKey);

        // Check if model is already downloaded
        try
        {
            var downloadedModelFolder = (SystemFolder)await modelFolder.GetFirstByNameAsync(model.Key, cancellationToken);
            Logger.LogInformation($"Model {model.Name} found in {downloadedModelFolder.Path}, loading");
            model = await VoiceModel.LoadModel(downloadedModelFolder.Path);
        }
        catch (FileNotFoundException)
        {
            // If model is not found, we will download it
            Logger.LogInformation($"Model {model.Name} not found, downloading to {modelFolder.Path}");
            await PiperDownloader.DownloadModel(model, modelFolder.Path);
        }

        // To start generating audio use PiperProvider
        var piperModel = new PiperProvider(new PiperConfiguration()
        {
            ExecutableLocation = downloadedPiperExecutable.Path, // Path to piper executable
            WorkingDirectory = downloadedPiperPlatformBinariesFolder.Path, // Working directory for piper
            Model = model, // Loaded/downloaded VoiceModel
        });

        Logger.LogInformation($"ExecutableLocation {piperModel.Configuration.ExecutableLocation}");
        Logger.LogInformation($"ModelLocation {piperModel.Configuration.Model.ModelLocation}");
        Logger.LogInformation($"WorkingDirectory {piperModel.Configuration.WorkingDirectory}");
        Logger.LogInformation($"BuildArguments {piperModel.Configuration.BuildArguments()}");
        return piperModel;
    }

    private async Task PlayAudioAsync(IFile file, string? outputDevice, CancellationToken cancellationToken = default)
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
            // Unknown device: print the available names instead of crashing on an invalid open.
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
