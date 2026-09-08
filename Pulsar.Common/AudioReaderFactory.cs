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
            // NAudio's SoundFileReader uses sf_open, which doesn't support UTF-8, apparently.
            // So we open ourselves in .NET land, then pass the stream to NAudio.
            return new OwnedSoundFileReader(File.OpenRead(path));
        }
        catch (Exception e)
        {
            // libsndfile doesn't support AAC/M4A/WMA/ALAC, but Windows Media Foundation does.
            Log.Debug("libsndfile declined {GetFileName} ({message}); trying Media Foundation.", Path.GetFileName(path),
                      e.Message);
            return new MediaFoundationReader(path);
        }
    }

    /// <summary>
    /// Open raw bytes, used for .scd. Only uses libsndfile, since we only support loading Vorbis .scd's.
    /// </summary>
    public static WaveStream OpenBytes(byte[] data) => new OwnedSoundFileReader(new MemoryStream(data, false));

    // SoundFileReader leaves its input stream open. Own both resources for the returned reader's lifetime.
    private sealed class OwnedSoundFileReader : WaveStream, ISampleProvider
    {
        private readonly Stream input;
        private readonly SoundFileReader reader;

        public OwnedSoundFileReader(Stream input)
        {
            this.input = input;
            try
            {
                reader = new SoundFileReader(input);
            }
            catch
            {
                input.Dispose();
                throw;
            }
        }

        public override WaveFormat WaveFormat => reader.WaveFormat;
        public override bool CanSeek => reader.CanSeek;
        public override long Length => reader.Length;
        public override long Position
        {
            get => reader.Position;
            set => reader.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => reader.Read(buffer);
        public int Read(Span<float> buffer) => reader.Read(buffer);

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    try
                    {
                        reader.Dispose();
                    }
                    finally
                    {
                        input.Dispose();
                    }
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
