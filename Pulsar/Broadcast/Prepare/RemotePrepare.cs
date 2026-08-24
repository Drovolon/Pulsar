using System;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Common.Api;
using Pulsar.Playback;

namespace Pulsar.Broadcast.Prepare;

/// <summary>
/// Dispatch, same as RemoteEngineLoad: if .scd, parse plugin-side (using Dalamud's instance of Lumina)
/// and ship the raw bytes over the pipe to the transcode host. See RemoteEngineLoad / ScdReader.
/// </summary>
internal static class RemotePrepare
{
    extension(IPrepareService prep)
    {
        public Task<PreparedTrack> PrepareFileAsync(string path, string transcodeOutPath, CancellationToken ct) =>
            prep.PrepareFileAsync(path, transcodeOutPath, ScdReader.ExtractAudio, ct);

        internal Task<PreparedTrack> PrepareFileAsync(
            string path, string transcodeOutPath, Func<string, byte[]> extractAudio, CancellationToken ct)
        {
            if (ScdReader.IsScd(path))
            {
                try
                {
                    return prep.PrepareBytesAsync(path, extractAudio(path), transcodeOutPath, ct);
                }
                catch (Exception ex)
                {
                    // Fall through to the path prep: the host can't decode it either, but its
                    // failure keeps all prep errors flowing through the one PrepResult.Failed path.
                    Plugin.Log.Warning(ex, "SCD extraction failed for {path}; handing the path to the host anyway", path);
                }
            }

            return prep.PrepareAsync(path, transcodeOutPath, ct);
        }
    }
}
