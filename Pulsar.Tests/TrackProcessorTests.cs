using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Blake3;
using Pulsar.TranscodeHost.Prepare;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// The real prepare pipeline (decision, resample, loudness, cleanup) over generated fixtures.
/// </summary>
public class TrackProcessorTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-transcode-test-");
    public void Dispose() => dir.Delete(recursive: true);

    private string P(string name) => Path.Combine(dir.FullName, name);

    private static string Blake3Of(string path)
    {
        var hash = Hasher.Hash(File.ReadAllBytes(path));
        return Convert.ToHexString(hash.AsSpan());
    }

    private static string Sha1Of(string path)
        => Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(path)));

    // A full-scale 997 Hz sine measures ≈ -3.01 LUFS; amplitude scales it linearly in dB.
    private static double ExpectedLufs(double amplitude) => (20 * Math.Log10(amplitude)) - 3.01;

    // libsndfile's Opus VBR quality is a [0,1] double solved from a per-channel target
    // bitrate: quality = (bps - 6000) / 250_000, clamped.
    [Theory]
    [InlineData(6_000, 0.0)]       // exact floor
    [InlineData(80_000, 0.296)]    // our stereo default: 160k/2
    [InlineData(1_000_000, 1.0)]   // clamps high
    [InlineData(0, 0.0)]           // clamps low
    public void Opus_quality_solves_for_the_target_bitrate(int bpsPerChannel, double expected)
        => Assert.Equal(expected, TrackProcessor.OpusQualityForBitrate(bpsPerChannel), 3);

    [Fact]
    public void High_bitrate_input_is_transcoded_to_the_out_path()
    {
        // 44.1k stereo 16-bit PCM ≈ 1411 kbps, far over the 256 kbps threshold. And
        // 44100 isn't Opus-legal: completing at all proves the resample branch too.
        var src = TestAudio.WriteWav(P("hi.wav"), 44100, 2, 2, TestAudio.Sine(44100, 0.5));
        var outPath = P("hi.transcoded");

        var track = TrackProcessor.Process(src, outPath, CancellationToken.None);

        Assert.Equal(outPath, track.SyncPath);
        Assert.True(File.Exists(outPath), "transcoded artifact written");
        Assert.Equal(Blake3Of(outPath), track.Blake3Hash);
        Assert.Equal(Sha1Of(outPath), track.Sha1Hash);
    }

    [Fact]
    public void Low_bitrate_input_passes_through_untouched()
    {
        // 5s of FLAC silence: a few KB for 5s ≈ single-digit kbps, far under threshold.
        var src = TestAudio.WriteQuietFlac(P("lo.flac"), 48000, 1, 5);
        var outPath = P("lo.transcoded");

        var track = TrackProcessor.Process(src, outPath, CancellationToken.None);

        Assert.Equal(src, track.SyncPath);                      // the DJ's own file syncs as-is
        Assert.False(File.Exists(outPath), "no artifact for a passthrough");
        Assert.Equal(Blake3Of(src), track.Blake3Hash);
        Assert.Equal(Sha1Of(src), track.Sha1Hash);
    }

    [Fact]
    public void Loudness_gain_targets_minus_18_lufs()
    {
        var src = TestAudio.WriteWav(P("tone.wav"), 48000, 1, 5, TestAudio.Sine(48000, 0.1));
        var track = TrackProcessor.Process(src, P("tone.t"), CancellationToken.None);

        var expected = -18.0 - ExpectedLufs(0.1); // ≈ +5.0 dB boost for the quiet tone
        Assert.InRange(track.GainDb, expected - 0.5, expected + 0.5);
    }

    [Fact]
    public void Passthrough_still_measures_loudness()
    {
        // The no-transcode path must DRAIN the analyzer tap, not skip it: an audible
        // sine in a low-bitrate FLAC still gets a real gain.
        var src = TestAudio.WriteQuietFlac(P("quiet-tone.flac"), 8000, 1, 5, amplitude: 0.1);
        var track = TrackProcessor.Process(src, P("qt.t"), CancellationToken.None);

        Assert.Equal(src, track.SyncPath);
        var expected = -18.0 - ExpectedLufs(0.1);
        Assert.InRange(track.GainDb, expected - 1.0, expected + 1.0); // wider: 8k rate edge
    }

    [Fact]
    public void Silence_gets_no_gain()
    {
        var src = TestAudio.WriteWav(P("silence.wav"), 48000, 1, 2, TestAudio.Silence);
        var track = TrackProcessor.Process(src, P("s.t"), CancellationToken.None);
        Assert.Equal(0, track.GainDb); // the -70 LUFS guard: never "boost" silence by +52 dB
    }

    [Fact]
    public void Cancellation_leaves_no_partial_files()
    {
        var src = TestAudio.WriteWav(P("c.wav"), 48000, 2, 2, TestAudio.Sine(48000, 0.5));
        var outPath = P("c.transcoded");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => TrackProcessor.Process(src, outPath, cts.Token));

        Assert.False(File.Exists(outPath), "no output on cancellation");
        Assert.False(File.Exists(outPath + ".tmp"), "no orphaned .tmp");
    }

    [Fact]
    public void Bytes_and_path_entry_points_agree()
    {
        var src = TestAudio.WriteWav(P("b.wav"), 48000, 1, 2, TestAudio.Sine(48000, 0.25));
        var fromPath = TrackProcessor.Process(src, P("b1.t"), CancellationToken.None);
        var fromBytes = TrackProcessor.ProcessBytes(
            src, File.ReadAllBytes(src), P("b2.t"), CancellationToken.None);

        Assert.Equal(fromPath.GainDb, fromBytes.GainDb, 1); // same audio, same measurement
    }
}
