using NAudio.CoreAudioApi;
using Serilog;

namespace Pulsar.AudioHost.Playback;

/// <summary>
/// Try to set the audio session display name for the volume mixer. This doesn't appear to work
/// for me under Wine on Linux; who knows if it actually works on Windows.
/// TODO: ask someone to test that.
/// </summary>
public static class MixerIdentity
{
    public static string? Name { get; set; }

    public static void TryApply()
    {
        if (Name is null) return;
        try
        {
            // Same endpoint selection as WasapiPlayerBuilder's default (Render/Console).
            var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            device.AudioSessionManager.AudioSessionControl.DisplayName = Name;
        }
        catch (Exception e)
        {
            Log.Debug("Could not set mixer display name: {message}", e.Message);
        }
    }
}
