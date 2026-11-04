using System.CommandLine;
using OwlCore.Storage.System.IO;
using OwlCore.Console.Storage.Extensions;

namespace OwlCore.Console.TalkBox.Commands.Play;

public class PlayCommand : Command
{
    private readonly CancellationToken cancellationToken;

    /// <summary>
    /// Creates a new instance of <see cref="PlayCommand"/>.
    /// </summary>
    /// <param name="cancellationToken"></param>
    public PlayCommand(CancellationToken cancellationToken)
        : base("play", "Play a wav file.")
    {
        this.cancellationToken = cancellationToken;

        var wavOption = new Option<string?>("--wav")
        {
            DefaultValueFactory = _ => null,
            Description = "The path to a WAV file to play."
        };

        var outputDeviceOption = new Option<string?>("--outputDevice")
        {
            DefaultValueFactory = _ => null,
            Description = "The OpenAL playback device name to route playback to. Leave out to list available devices; pass 'default' for the default device."
        };

        this.SetAction(x => PlayAsync(
            x.GetValue(wavOption),
            x.GetValue(outputDeviceOption)));

        this.Options.Add(wavOption);
        this.Options.Add(outputDeviceOption);
    }

    public async Task PlayAsync(string? wav, string? outputDevice)
    {
        if (wav is null)
            return;

        var audioFile = new SystemFile(wav);
        await audioFile.PlayWavAsync(outputDevice, this.cancellationToken);
    }
}
