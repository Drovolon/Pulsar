using NAudio.SoundFile;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Pulsar.Common;
using Pulsar.Common.Api;

namespace Pulsar.TranscodeHost.Prepare;

/// <summary>
/// Feeds each decoded buffer into a <see cref="LoudnessAnalyzer"/> as it passes through, and honours
/// cancellation. Used both to drive the Opus encoder and to drive a bare analysis pass.
/// </summary>
internal sealed class AnalyzerTap(ISampleProvider source, LoudnessAnalyzer analyzer, CancellationToken ct)
    : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        ct.ThrowIfCancellationRequested();
        var n = source.Read(buffer);
        if (n > 0) analyzer.AddFrames(buffer[..n]);
        return n;
    }
}

public static class TrackProcessor
{
    public const long BitrateThresholdBpsPerChannel = 256_000 / 2; // >=256 Kbps (stereo) gets transcoded
    public const int OpusTargetBpsPerChannel = 160_000 / 2; // 160 Kbps for stereo
    public const double TargetLufs = -18.0; // matches ReplayGain
    public const double PeakCeilingDb = -1.0;
    
    /// <summary>
    /// Decodes a file, transcodes it to Opus if it's higher than the bitrate threshold, and does ReplayGain
    /// loudness measurement to keep listening consistent.
    /// </summary>
    public static PreparedTrack Process(
        string originalPath, string transcodeOutPath, CancellationToken ct)
    {
        using var reader = AudioReaderFactory.Open(originalPath);
        return Process(reader, new FileInfo(originalPath).Length, originalPath, transcodeOutPath, ct);
    }

    /// <summary>
    /// Used to process raw Vorbis bytes from .scd files.
    /// </summary>
    public static PreparedTrack ProcessBytes(
        string originalPath, byte[] audioData, string transcodeOutPath, CancellationToken ct)
    {
        using var reader = AudioReaderFactory.OpenBytes(audioData);
        return Process(reader, audioData.LongLength, originalPath, transcodeOutPath, ct);
    }

    private static PreparedTrack Process(
        WaveStream reader, long sizeBytes, string passthroughPath, string transcodeOutPath, CancellationToken ct)
    {
        var sp = reader.ToSampleProvider();
        var fmt = sp.WaveFormat;

        var durMs = reader.TotalTime.TotalMilliseconds;
        var transcode = durMs <= 0 || (sizeBytes * 8000.0 / durMs) > BitrateThresholdBpsPerChannel * fmt.Channels;

        using var analyzer = new LoudnessAnalyzer(fmt.Channels, fmt.SampleRate);
        var tap = new AnalyzerTap(sp, analyzer, ct);

        string syncPath;
        if (transcode)
        {
            // Opus has strict sample rate requirements, so resample if needed
            ISampleProvider encodeSource = fmt.SampleRate is 8000 or 12000 or 16000 or 24000 or 48000
                ? tap
                : new WdlResamplingSampleProvider(tap, 48000);
            var tmp = transcodeOutPath + ".tmp";
            try
            {
                var quality = OpusQualityForBitrate(OpusTargetBpsPerChannel);
                SoundFileWriter.CreateSoundFile(tmp, encodeSource.ToWaveProvider(),
                    SoundFileMajorFormat.Opus, new SoundFileWriterOptions { VbrQuality = quality });
                File.Move(tmp, transcodeOutPath, overwrite: true);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }
            syncPath = transcodeOutPath;
        }
        else
        {
            // No transcode needed, but still do the loudness measurement.
            var buf = new float[fmt.SampleRate * Math.Max(1, fmt.Channels)];
            while (tap.Read(buf) > 0)
            {
                // AnalyzerTap still does loudness measure. Drain the tap.
            }
            syncPath = passthroughPath;
        }

        var (lufs, peakDb) = analyzer.Result();
        double gainDb = 0;
        if (!double.IsInfinity(lufs) && lufs > -70.0)
        {
            gainDb = TargetLufs - lufs;
            gainDb = Math.Min(gainDb, PeakCeilingDb - peakDb);
            gainDb = Math.Round(gainDb, 2);
        }
        return new PreparedTrack(syncPath, gainDb);
    }

    // VBR quality in libsndfile is a [0, 1] double. But I want a target bitrate, mostly because
    // that's what wiki.xiph.org and the HydrogenAudio forum talk in. Looking at the libsndfile source,
    // src/sndfile.c sets
    //     q = 1 - quality,
    // then src/ogg_opus.c sets
    //    target_bitrate = ((1 - q) * 250_000 + 6000) * channels
    // so this method solves for quality, given a target_bitrate
    //
    // Additionally, because it's a per-channel formula, the target bitrate is per-channel as well,
    // taking the guidance of 160 Kbps, etc., as stereo numbers. 160 Kbps total for a 7.1 file would be
    // a tiny, tiny bitrate - unlistenable.
    private static double OpusQualityForBitrate(int targetBpsPerChannel)
    {
        return Math.Clamp((targetBpsPerChannel - 6000) / 250_000.0, 0.0, 1.0);
    }
}
