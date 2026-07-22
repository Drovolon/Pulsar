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
    private sealed record DeferredLoad(string Path, TimeSpan Position, bool Playing, long PlaybackId);

    private readonly List<Call> calls = [];
    private readonly Lock @lock = new();

    private PlaybackState state = PlaybackState.Stopped;
    private string? path;
    private TimeSpan position;
    private long playbackId;
    private DeferredLoad? deferredLoad;

    /// <summary>Accept Load like AudioServer does, but wait to commit it until CompleteDeferredLoad.</summary>
    public bool DeferLoads { get; set; }

    /// <summary>Commit command state without publishing OnChanged, modeling a missed event feed.</summary>
    public bool SuppressChangedEvents { get; set; }

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
                    DateTimeOffset.UtcNow,
                    playbackId);
            }
        }
    }

    public event EventHandler<PlaybackEnded>? OnPlaybackEnded;
    public event EventHandler<EngineSnapshot>? OnChanged;

    public Task<bool> WaitForCall(string op, TimeSpan? timeout = null)
        => TestWait.Until(() => Ops.Contains(op), timeout);

    private void Enter(string op, object? arg = null)
    {
        lock (@lock) calls.Add(new Call(op, arg));
        if (Intercept?.Invoke(op) is { } ex) throw ex;
    }

    public async Task LoadAsync(
        string loadPath, TimeSpan pos, bool startPlaying, long newPlaybackId, CancellationToken ct)
    {
        Enter("Load", (loadPath, pos, startPlaying));
        if (Stall?.Invoke("Load") is { } hang) await hang;
        if (DeferLoads)
        {
            lock (@lock)
            {
                deferredLoad = new DeferredLoad(loadPath, pos, startPlaying, newPlaybackId);
            }
            return;
        }
        CommitLoad(loadPath, pos, startPlaying, newPlaybackId);
    }

    public void CompleteDeferredLoad()
    {
        DeferredLoad? pending;
        lock (@lock)
        {
            pending = deferredLoad;
            deferredLoad = null;
        }
        if (pending is not null)
            CommitLoad(pending.Path, pending.Position, pending.Playing, pending.PlaybackId);
    }

    private void CommitLoad(string loadPath, TimeSpan pos, bool startPlaying, long newPlaybackId)
    {
        lock (@lock)
        {
            path = loadPath;
            position = pos;
            playbackId = newPlaybackId;
            state = startPlaying ? PlaybackState.Playing : PlaybackState.Paused;
        }
        PublishChanged();
    }

    public Task LoadBytesAsync(
        string displayPath, byte[] audioData, TimeSpan pos,
        bool startPlaying, long newPlaybackId, CancellationToken ct)
    {
        Enter("LoadBytes", (displayPath, pos, startPlaying));
        lock (@lock)
        {
            path = displayPath;
            LastLoadedBytes = audioData;
            position = pos;
            playbackId = newPlaybackId;
            state = startPlaying ? PlaybackState.Playing : PlaybackState.Paused;
        }
        PublishChanged();
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
        PublishChanged();
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
        if (changed) PublishChanged();
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
        if (changed) PublishChanged();
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
        if (changed) PublishChanged();
        return Task.CompletedTask;
    }

    public async Task<EngineSnapshot> GetStateAsync(CancellationToken ct)
    {
        if (Intercept?.Invoke("GetState") is { } ex) throw ex;
        if (Stall?.Invoke("GetState") is { } hang) await hang;
        return Snapshot;
    }

    /// <summary>Simulate the loaded track playing to its natural end.</summary>
    public void FinishTrack() => EndTrack(EndReason.Finished);

    /// <summary>Simulate the loaded track failing mid-play (or a load failure).</summary>
    public void FailTrack() => EndTrack(EndReason.Failed);

    /// <summary>Simulate the audio host disappearing with a track loaded.</summary>
    public void DisconnectTrack() => EndTrack(EndReason.Disconnected);

    /// <summary>Raise an event from an older playback without changing current engine state.</summary>
    public void RaisePlaybackEnded(long endedPlaybackId, EndReason reason)
        => OnPlaybackEnded?.Invoke(this, new PlaybackEnded(endedPlaybackId, reason));

    /// <summary>Raise an arbitrary engine observation without changing current engine state.</summary>
    public void RaiseChanged(EngineSnapshot snapshot) => OnChanged?.Invoke(this, snapshot);

    private void PublishChanged()
    {
        if (!SuppressChangedEvents) OnChanged?.Invoke(this, Snapshot);
    }

    private void EndTrack(EndReason reason)
    {
        long endedPlaybackId;
        lock (@lock)
        {
            // The real host cannot end a track it isn't playing (no trackEnded TCS).
            if (path is null) return;
            path = null;
            position = TimeSpan.Zero;
            state = PlaybackState.Stopped;
            endedPlaybackId = playbackId;
        }
        OnPlaybackEnded?.Invoke(this, new PlaybackEnded(endedPlaybackId, reason));
    }
}
