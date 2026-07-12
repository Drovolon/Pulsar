using System;
using Pulsar.TranscodeHost.Prepare;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// LoudnessAnalyzer over the real native libebur128 (system .so on Linux; same
/// 1.2.6 as the bundled Windows DLL). Tolerance-based: we verify OUR wiring, not the DSP.
/// </summary>
public class LoudnessAnalyzerTests
{
    private static void Feed(LoudnessAnalyzer analyzer, int channels, int rate, double seconds,
        Func<int, double>[] perChannel)
    {
        var frames = (int)(rate * seconds);
        var buf = new float[frames * channels];
        for (var i = 0; i < frames; i++)
            for (var c = 0; c < channels; c++)
                buf[(i * channels) + c] = (float)perChannel[c](i);
        analyzer.AddFrames(buf);
    }

    [Fact]
    public void Measures_a_known_tone()
    {
        using var a = new LoudnessAnalyzer(1, 48000);
        Feed(a, 1, 48000, 5, [TestAudio.Sine(48000, 0.1)]);
        var (lufs, peakDb) = a.Result();
        Assert.InRange(lufs, -24.0, -22.0);   // ≈ -23.01 (a -20 dBFS sine, K-weight ~0 dB @ 1 kHz)
        Assert.InRange(peakDb, -21.0, -19.0); // ≈ 20·log10(0.1)
    }

    [Fact]
    public void Silence_reads_below_the_gain_guard()
    {
        using var a = new LoudnessAnalyzer(1, 48000);
        Feed(a, 1, 48000, 1, [TestAudio.Silence]);
        var (lufs, _) = a.Result();
        // TrackProcessor's guard: anything <= -70 (or -inf) means "no gain, don't trust it".
        Assert.False(lufs > -70.0, $"silence measured {lufs} LUFS - the gain guard would misfire");
    }

    [Fact]
    public void True_peak_takes_the_loudest_channel()
    {
        using var a = new LoudnessAnalyzer(2, 48000);
        Feed(a, 2, 48000, 1, [TestAudio.Sine(48000, 0.01), TestAudio.Sine(48000, 0.5)]);
        var (_, peakDb) = a.Result();
        Assert.InRange(peakDb, -7.0, -5.0); // ≈ -6.02, from the RIGHT channel - not ch0
    }

    [Fact]
    public void Double_dispose_is_safe()
    {
        var a = new LoudnessAnalyzer(1, 48000);
        a.Dispose();
        a.Dispose(); // the ref-nint destroy zeroes state; second call must no-op
    }
}
