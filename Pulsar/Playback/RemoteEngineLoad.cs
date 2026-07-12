using System;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Common.Api;

namespace Pulsar.Playback;

/// <summary>
/// Dispatch: if SCD, parse locally plugin-side and ship bytes over the pipe to the audio host.
/// That's because we rely on Lumina via Dalamud to parse .scd files. See ScdReader.
/// </summary>
internal static class RemoteEngineLoad
{
    extension(IRemoteEngine engine)
    {
        public Task LoadFileAsync(
            string path, TimeSpan position,
            bool startPlaying, CancellationToken ct)
            =>
                engine.LoadFileAsync(path, position, startPlaying, ScdReader.ExtractAudio, ct);

        internal Task LoadFileAsync(
            string path, TimeSpan position,
            bool startPlaying, Func<string, byte[]> extractAudio, CancellationToken ct)
        {
            if (ScdReader.IsScd(path))
            {
                try
                {
                    return engine.LoadBytesAsync(path, extractAudio(path), position, startPlaying, ct);
                }
                catch (Exception ex)
                {
                    // The host won't be able to parse this either, but importantly this will trigger the
                    // "failed to load file" path, which will send an event back to us...
                    // This is a big hack. TODO: make it better.
                    Plugin.Log.Warning(ex, "SCD extraction failed for {path}; sending path anyway", path);
                }
            }

            return engine.LoadAsync(path, position, startPlaying, ct);
        }
    }
}
