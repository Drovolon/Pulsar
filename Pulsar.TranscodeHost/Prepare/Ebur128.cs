using System.Runtime.InteropServices;

namespace Pulsar.TranscodeHost.Prepare;

internal static class Ebur128Interop
{
    // Confirmed against the bundled ebur128.h (libebur128 1.2.6):
    //   MODE_M = 1<<0; MODE_I = (1<<2)|MODE_M = 5; TRUE_PEAK = (1<<5)|(1<<4)|MODE_M = 49.
    private const int MODE_M = 1 << 0;                                  // 1
    public const int MODE_I = (1 << 2) | MODE_M;                        // 5 (integrated loudness)
    public const int MODE_TRUE_PEAK = (1 << 5) | (1 << 4) | MODE_M;     // 49 (true peak, includes sample peak)

    [DllImport("libebur128", CallingConvention = CallingConvention.Cdecl)]
    public static extern nint ebur128_init(uint channels, uint samplerate, int mode);

    [DllImport("libebur128", CallingConvention = CallingConvention.Cdecl)]
    public static extern unsafe int ebur128_add_frames_float(nint st, float* src, nuint frames);

    [DllImport("libebur128", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ebur128_loudness_global(nint st, out double outVal);

    [DllImport("libebur128", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ebur128_true_peak(nint st, uint channel, out double outVal);

    [DllImport("libebur128", CallingConvention = CallingConvention.Cdecl)]
    public static extern void ebur128_destroy(ref nint st);
}

/// <summary>
/// Streams interleaved float frames into libebur128 and yields integrated loudness (LUFS)
/// plus the max true peak (dBTP). DJ-side only, on the DJ's own trusted files.
/// </summary>
internal sealed class LoudnessAnalyzer : IDisposable
{
    private readonly int channels;
    private nint state;

    public LoudnessAnalyzer(int channels, int sampleRate)
    {
        this.channels = channels;
        state = Ebur128Interop.ebur128_init((uint)channels, (uint)sampleRate,
                                            Ebur128Interop.MODE_I | Ebur128Interop.MODE_TRUE_PEAK);
        if (state == 0) throw new InvalidOperationException("ebur128_init failed");
    }

    /// <param name="interleaved">Interleaved float samples; length must be a multiple of channels.</param>
    public unsafe void AddFrames(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.IsEmpty) return;
        var frames = (nuint)(interleaved.Length / channels);
        fixed (float* p = interleaved)
            Ebur128Interop.ebur128_add_frames_float(state, p, frames);
    }

    /// <returns>(integrated LUFS, max true peak in dBTP). LUFS is a huge negative number for silence.</returns>
    public (double integratedLufs, double truePeakDb) Result()
    {
        if (Ebur128Interop.ebur128_loudness_global(state, out var lufs) != 0)
        {
            // If loudness calculation fails, set -inf as a sentinel, which upstream code will treat
            // as a failure case and ignore (rather than apply a spurious -18 gain, thinking lufs==0)
            lufs = double.NegativeInfinity;
        }
        double peak = 0;
        for (uint ch = 0; ch < channels; ch++)
        {
            if (Ebur128Interop.ebur128_true_peak(state, ch, out var p) == 0)
                peak = Math.Max(peak, p);
        }
        var peakDb = peak > 0 ? 20.0 * Math.Log10(peak) : -144.0;
        return (lufs, peakDb);
    }

    public void Dispose()
    {
        if (state != 0) Ebur128Interop.ebur128_destroy(ref state);
    }
}
