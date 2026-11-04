using OwlCore.ComponentModel;
using OwlCore.Storage;
using PiperSharp.Models;

namespace OwlCore.Console.TalkBox.Settings;

/// <summary>
/// Settings for Piper TTS
/// </summary>
public class PiperSettings : SettingsBase
{
    /// <summary>
    /// Creates a new instance of <see cref="PiperSettings"/>.
    /// </summary>
    /// <param name="folder">The folder to store piper settings in.</param>
    public PiperSettings(IModifiableFolder folder)
        : base(folder, new NewtonsoftSerializer())
    {
    }

    /// <summary>
    /// The voice models available from huggingface
    /// </summary>
    public List<VoiceModel> VoiceModels
    {
        get => GetSetting(() =>  new List<VoiceModel>());
        set => SetSetting(value);
    }

    public override Task SaveAsync(CancellationToken? cancellationToken = null)
    {
        // Force call to SetSetting so list updates are seen
        VoiceModels = VoiceModels;
        return base.SaveAsync(cancellationToken);
    }
}
