using System;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Common.Api;
using Pulsar.Playback;

namespace Pulsar.Broadcast.Prepare;

/// <summary>
/// The prep dispatch seam, mirroring RemoteEngineLoad: regular files go to the transcode
/// host by path, but .scd can only be parsed plugin-side (Lumina), so those ship as
/// extracted Vorbis bytes.
/// </summary>
internal static class RemotePrepare
{
    public static Task<PreparedTrack> PrepareFileAsync(this IPrepareService prep, string path,
                                                       string transcodeOutPath, CancellationToken ct)
    {
        if (ScdReader.IsScd(path))
        {
            try
            {
                return prep.PrepareBytesAsync(path, ScdReader.ExtractAudio(path), transcodeOutPath, ct);
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
