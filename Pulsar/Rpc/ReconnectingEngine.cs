using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Common.Api;
using StreamJsonRpc;

namespace Pulsar.Rpc;

/// <summary>
/// IRemoteEngine for general use in the plugin. Forwards to the underlying HostConnection.
/// While disconnected, calls throw ConnectionLostException.
/// </summary>
public sealed class ReconnectingEngine : IRemoteEngine, IAsyncDisposable
{
    private readonly HostConnection<IRemoteEngine> connection;

    private readonly Lock updateLock = new();
    private long activePlaybackId;
    private long activeSequence;

    public event EventHandler<EngineSnapshot>? OnUpdated;

    public event Action? OnReconnected;

    public ReconnectingEngine(HostSpec spec)
    {
        connection = new HostConnection<IRemoteEngine>(spec, WireProxy);
        connection.OnConnected += () => OnReconnected?.Invoke();
        connection.OnDisconnected += HandleDisconnected;
    }

    public void Start() => connection.Start();

    public ValueTask DisposeAsync() => connection.DisposeAsync();

    private void WireProxy(IRemoteEngine fresh)
    {
        fresh.OnUpdated += (_, update) =>
        {
            TrackUpdate(update);
            OnUpdated?.Invoke(this, update);
        };
    }

    private void HandleDisconnected()
    {
        long playbackId;
        long sequence;
        lock (updateLock)
        {
            playbackId = activePlaybackId;
            sequence = activeSequence + 1;
            activePlaybackId = 0;
            activeSequence = 0;
        }
        if (playbackId == 0) return;
        OnUpdated?.Invoke(this, new EngineSnapshot(
            PlaybackState.Stopped,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            playbackId,
            sequence,
            EndReason.Disconnected));
    }

    private IRemoteEngine Live => connection.Proxy
        ?? throw new ConnectionLostException($"{connection.HostName} is not connected");

    public async Task LoadAsync(
        string path, TimeSpan position, bool startPlaying, long playbackId, CancellationToken ct)
    {
        BeginPlayback(playbackId);
        try
        {
            await Live.LoadAsync(path, position, startPlaying, playbackId, ct);
        }
        catch
        {
            RetirePlayback(playbackId);
            throw;
        }
    }

    public async Task LoadBytesAsync(
        string displayPath, byte[] audioData, TimeSpan position,
        bool startPlaying, long playbackId, CancellationToken ct)
    {
        BeginPlayback(playbackId);
        try
        {
            await Live.LoadBytesAsync(
                displayPath, audioData, position, startPlaying, playbackId, ct);
        }
        catch
        {
            RetirePlayback(playbackId);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await Live.StopAsync(ct);
        lock (updateLock)
        {
            activePlaybackId = 0;
            activeSequence = 0;
        }
    }

    public Task PauseAsync(CancellationToken ct) => Live.PauseAsync(ct);
    public Task ResumeAsync(CancellationToken ct) => Live.ResumeAsync(ct);
    public Task SetVolumeAsync(float volume, CancellationToken ct) => Live.SetVolumeAsync(volume, ct);
    public Task SeekAsync(TimeSpan position, CancellationToken ct) => Live.SeekAsync(position, ct);
    public async Task<EngineSnapshot> GetStateAsync(CancellationToken ct)
    {
        var update = await Live.GetStateAsync(ct);
        TrackUpdate(update);
        return update;
    }

    private void TrackUpdate(EngineSnapshot update)
    {
        lock (updateLock)
        {
            if (activePlaybackId != update.PlaybackId) return;
            activeSequence = Math.Max(activeSequence, update.Sequence);
            if (update.State != PlaybackState.Stopped) return;
            activePlaybackId = 0;
            activeSequence = 0;
        }
    }

    private void BeginPlayback(long playbackId)
    {
        lock (updateLock)
        {
            activePlaybackId = playbackId;
            activeSequence = 0;
        }
    }

    private void RetirePlayback(long playbackId)
    {
        lock (updateLock)
        {
            if (activePlaybackId != playbackId) return;
            activePlaybackId = 0;
            activeSequence = 0;
        }
    }
}
