using System;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Common.Api;

namespace Pulsar.Playback;

/// <summary>
/// The load dispatch seam: regular files go to the audio host by path, but .scd can only
/// be parsed plugin-side (Lumina), so those ship as extracted Vorbis bytes instead.
/// </summary>
internal static class RemoteEngineLoad
{
    public static Task LoadFileAsync(this IRemoteEngine engine, string path, TimeSpan position,
                                     bool startPlaying, CancellationToken ct)
    {
        if (ScdReader.IsScd(path))
        {
            try
            {
                return engine.LoadBytesAsync(path, ScdReader.ExtractAudio(path), position, startPlaying, ct);
            }
            catch (Exception ex)
            {
                // No reason not to try it, at least. And that will use all the same track load failure machinery.
                Plugin.Log.Warning(ex, "SCD extraction failed for {path}; sending path anyway", path);
            }
        }

        return engine.LoadAsync(path, position, startPlaying, ct);
    }
}
