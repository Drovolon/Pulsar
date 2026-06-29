using NAudio.SoundFile;
using NAudio.Wave;
using Serilog;

namespace Pulsar.Common;

/// <summary>
/// Decides which decoder to use for a file using magic bytes + trying libsndfile + falling back to WFM.
///
/// * .scd -> parsed plugin-side by Lumina (in-game only); the extracted Vorbis arrives here as bytes
/// * .wav, .aiff, .flac, .ogg (vorbis), .opus/.ogg.opus, .mp3 -> libsndfile
/// * .aac, .m4a, .wma, .alac -> Windows Media Foundation
/// </summary>
public static class AudioReaderFactory
{
    public static WaveStream Open(string path)
    {
        try
        {
            // Try libsndfile first. We ship the .dll, so this should be cross-platform (for what it supports)
            return new SoundFileReader(path);
        }
        catch (Exception e)
        {
            // libsndfile doesn't support AAC/M4A/WMA/ALAC, but Windows Media Foundation does.
            Log.Debug("libsndfile declined {GetFileName} ({message}); trying Media Foundation.", Path.GetFileName(path), e.Message);
            return new MediaFoundationReader(path);
        }
    }

    /// <summary>
    /// Open raw bytes, used for .scd. Only uses libsndfile, since we only support loading Vorbis .scd's.
    /// </summary>
    public static WaveStream OpenBytes(byte[] data)
    {
        return new SoundFileReader(new MemoryStream(data, writable: false));
    }
}
