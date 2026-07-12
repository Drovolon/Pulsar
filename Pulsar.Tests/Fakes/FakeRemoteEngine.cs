using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Common.Api;

namespace Pulsar.Tests.Fakes;

/// <summary>
/// In-memory IRemoteEngine that mirrors FilePlayer's observable semantics: commands
/// commit state, then OnChanged fires with the post-transition snapshot (loads, stop,
/// pause, resume, seek - not volume). Natural track end is simulated via FinishTrack/
/// FailTrack, which unload and fire OnPlaybackEnded, exactly like the real host.
/// </summary>
public sealed class FakeRemoteEngine : IRemoteEngine
{
    public sealed record Call(string Op, object? Arg = null);

    private readonly List<Call> calls = [];
    private readonly Lock @lock = new();

    private PlaybackState state = PlaybackState.Stopped;
    private string? path;
    private TimeSpan position;

    /// <summary>Reported total track length while something is loaded.</summary>
    public TimeSpan TrackDuration { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Latest volume the engine was told to use; -1 = never set.</summary>
    public float LastVolume { get; private set; } = -1f;

    /// <summary>Raw payload from the latest LoadBytes call, for SCD dispatch assertions.</summary>
    public byte[]? LastLoadedBytes { get; private set; }

    /// <summary>Optional failure injection: return an exception to throw for the given op.</summary>
    public Func<string, Exception?>? Intercept { get; set; }

    /// <summary>Optional hang injection: return a task to await for the given op
    /// (a never-completing task models a wedged-but-connected host).</summary>
    public Func<string, Task?>? Stall { get; set; }

    public IReadOnlyList<Call> Calls
    {
        get { lock (@lock) return [.. calls]; }
    }

    public IReadOnlyList<string> Ops => [.. Calls.Select(c => c.Op)];

    public EngineSnapshot Snapshot
    {
        get
        {
            lock (@lock)
            {
                return new EngineSnapshot(
                    state, path, null,
                    path is null ? null : new PlaybackPosition(position, TrackDuration),
                    DateTimeOffset.UtcNow);
            }
        }
    }

    public event EventHandler<EndReason>? OnPlaybackEnded;
    public event EventHandler<EngineSnapshot>? OnChanged;

    public Task<bool> WaitForCall(string op, TimeSpan? timeout = null)
        => TestWait.Until(() => Ops.Contains(op), timeout);

    private void Enter(string op, object? arg = null)
    {
        lock (@lock) calls.Add(new Call(op, arg));
        if (Intercept?.Invoke(op) is { } ex) throw ex;
    }

    public Task LoadAsync(string loadPath, TimeSpan pos, bool startPlaying, CancellationToken ct)
    {
        Enter("Load", (loadPath, pos, startPlaying));
        lock (@lock)
        {
            path = loadPath;
            position = pos;
            state = startPlaying ? PlaybackState.Playing : PlaybackState.Paused;
        }
        OnChanged?.Invoke(this, Snapshot);
        return Task.CompletedTask;
    }

    public Task LoadBytesAsync(string displayPath, byte[] audioData, TimeSpan pos, bool startPlaying, CancellationToken ct)
    {
        Enter("LoadBytes", (displayPath, pos, startPlaying));
        lock (@lock)
        {
            path = displayPath;
            LastLoadedBytes = audioData;
            position = pos;
            state = startPlaying ? PlaybackState.Playing : PlaybackState.Paused;
        }
        OnChanged?.Invoke(this, Snapshot);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        Enter("Stop");
        if (Stall?.Invoke("Stop") is { } hang) await hang;
        lock (@lock)
        {
            path = null;
            position = TimeSpan.Zero;
            state = PlaybackState.Stopped;
        }
        OnChanged?.Invoke(this, Snapshot);
    }

    public Task PauseAsync(CancellationToken ct)
    {
        Enter("Pause");
        var changed = false;
        lock (@lock)
        {
            if (state == PlaybackState.Playing) { state = PlaybackState.Paused; changed = true; }
        }
        // Like FilePlayer: no-op commands raise no OnChanged.
        if (changed) OnChanged?.Invoke(this, Snapshot);
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct)
    {
        Enter("Resume");
        var changed = false;
        lock (@lock)
        {
            if (state == PlaybackState.Paused) { state = PlaybackState.Playing; changed = true; }
        }
        if (changed) OnChanged?.Invoke(this, Snapshot);
        return Task.CompletedTask;
    }

    public async Task SetVolumeAsync(float volume, CancellationToken ct)
    {
        Enter("SetVolume", volume);
        if (Stall?.Invoke("SetVolume") is { } hang) await hang;
        LastVolume = volume;
    }

    public Task SeekAsync(TimeSpan pos, CancellationToken ct)
    {
        Enter("Seek", pos);
        var changed = false;
        lock (@lock)
        {
            if (path is not null) { position = pos; changed = true; }
        }
        if (changed) OnChanged?.Invoke(this, Snapshot);
        return Task.CompletedTask;
    }

    public Task<EngineSnapshot> GetStateAsync(CancellationToken ct)
    {
        if (Intercept?.Invoke("GetState") is { } ex) throw ex;
        return Task.FromResult(Snapshot);
    }

    /// <summary>Simulate the loaded track playing to its natural end.</summary>
    public void FinishTrack() => EndTrack(EndReason.Finished);

    /// <summary>Simulate the loaded track failing mid-play (or a load failure).</summary>
    public void FailTrack() => EndTrack(EndReason.Failed);

    private void EndTrack(EndReason reason)
    {
        lock (@lock)
        {
            // The real host cannot end a track it isn't playing (no trackEnded TCS).
            if (path is null) return;
            path = null;
            position = TimeSpan.Zero;
            state = PlaybackState.Stopped;
        }
        OnPlaybackEnded?.Invoke(this, reason);
    }
}
