using System;
using NAudio.Wave;

namespace Pulsar.Tests.Fakes;

/// <summary>
/// IWavePlayer with WasapiOut's observable semantics, verified against the NAudio source:
/// Stop() is a no-op when the device was never started (WasapiOut.Stop guards on
/// playbackState != Stopped, and a never-Play()ed device is still Stopped), and
/// PlaybackStopped is only raised from the play thread, which Play() creates.
/// </summary>
public sealed class FakeWavePlayer : IWavePlayer
{
    private volatile PlaybackState state = PlaybackState.Stopped;

    public PlaybackState PlaybackState => state;
    public float Volume { get; set; } = 1f;
    public WaveFormat OutputWaveFormat { get; private set; } = new(44100, 16, 2);

    /// <summary>The audio pipeline handed to Init - tests can pull samples through it.</summary>
    public IWaveProvider? Provider { get; private set; }
    public Action? BeforeInit { get; set; }

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider waveProvider)
    {
        BeforeInit?.Invoke();
        Provider = waveProvider;
        OutputWaveFormat = waveProvider.WaveFormat;
    }

    public void Play() => state = PlaybackState.Playing;

    public void Pause()
    {
        if (state == PlaybackState.Playing) state = PlaybackState.Paused;
    }

    public void Stop()
    {
        if (state == PlaybackState.Stopped) return; // never started (or already stopped): no event
        state = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }

    /// <summary>The play thread reached the end of the stream (or died): it raises
    /// PlaybackStopped on its own, without anyone calling Stop().</summary>
    public void SimulateNaturalEnd(Exception? error = null)
    {
        state = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs(error));
    }

    public void Dispose() { }
}
