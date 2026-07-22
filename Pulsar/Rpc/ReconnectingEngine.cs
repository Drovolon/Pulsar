using System;
using System.Threading;
using System.Threading.Tasks;
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

    // Last loaded playback ID (that hasn't been stopped/ended)
    private long activePlaybackId;

    public event EventHandler<PlaybackEnded>? OnPlaybackEnded;
    public event EventHandler<EngineSnapshot>? OnChanged;

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
        fresh.OnChanged += (_, snapshot) => OnChanged?.Invoke(this, snapshot);
        fresh.OnPlaybackEnded += (_, ended) =>
        {
            Interlocked.CompareExchange(ref activePlaybackId, 0, ended.PlaybackId);
            OnPlaybackEnded?.Invoke(this, ended);
        };
    }

    private void HandleDisconnected()
    {
        var playbackId = Interlocked.Exchange(ref activePlaybackId, 0);
        if (playbackId == 0) return;
        OnPlaybackEnded?.Invoke(this, new PlaybackEnded(playbackId, EndReason.Disconnected));
    }

    private IRemoteEngine Live => connection.Proxy
        ?? throw new ConnectionLostException($"{connection.HostName} is not connected");

    public async Task LoadAsync(
        string path, TimeSpan position, bool startPlaying, long playbackId, CancellationToken ct)
    {
        Interlocked.Exchange(ref activePlaybackId, playbackId);
        try
        {
            await Live.LoadAsync(path, position, startPlaying, playbackId, ct);
        }
        catch
        {
            Interlocked.CompareExchange(ref activePlaybackId, 0, playbackId);
            throw;
        }
    }

    public async Task LoadBytesAsync(
        string displayPath, byte[] audioData, TimeSpan position,
        bool startPlaying, long playbackId, CancellationToken ct)
    {
        Interlocked.Exchange(ref activePlaybackId, playbackId);
        try
        {
            await Live.LoadBytesAsync(
                displayPath, audioData, position, startPlaying, playbackId, ct);
        }
        catch
        {
            Interlocked.CompareExchange(ref activePlaybackId, 0, playbackId);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await Live.StopAsync(ct);
        Interlocked.Exchange(ref activePlaybackId, 0);
    }

    public Task PauseAsync(CancellationToken ct) => Live.PauseAsync(ct);
    public Task ResumeAsync(CancellationToken ct) => Live.ResumeAsync(ct);
    public Task SetVolumeAsync(float volume, CancellationToken ct) => Live.SetVolumeAsync(volume, ct);
    public Task SeekAsync(TimeSpan position, CancellationToken ct) => Live.SeekAsync(position, ct);
    public Task<EngineSnapshot> GetStateAsync(CancellationToken ct) => Live.GetStateAsync(ct);
}
