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

    // True if a load was accepted and no explicit stop/end has happened since.
    // Used to synthesize a playback ended event if the audio host crashes.
    private volatile bool trackActive;

    public event EventHandler<EndReason>? OnPlaybackEnded;
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
        fresh.OnPlaybackEnded += (_, reason) =>
        {
            trackActive = false;
            OnPlaybackEnded?.Invoke(this, reason);
        };
    }

    private void HandleDisconnected()
    {
        if (!trackActive) return;
        trackActive = false;
        // If the host died with a track loaded, fire a failed track event
        OnPlaybackEnded?.Invoke(this, EndReason.Failed);
    }

    private IRemoteEngine Live => connection.Proxy
        ?? throw new ConnectionLostException($"{connection.HostName} is not connected");

    public async Task LoadAsync(string path, TimeSpan position, bool startPlaying, CancellationToken ct)
    {
        await Live.LoadAsync(path, position, startPlaying, ct);
        trackActive = true;
    }

    public async Task LoadBytesAsync(string displayPath, byte[] audioData, TimeSpan position, bool startPlaying, CancellationToken ct)
    {
        await Live.LoadBytesAsync(displayPath, audioData, position, startPlaying, ct);
        trackActive = true;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await Live.StopAsync(ct);
        trackActive = false;
    }

    public Task PauseAsync(CancellationToken ct) => Live.PauseAsync(ct);
    public Task ResumeAsync(CancellationToken ct) => Live.ResumeAsync(ct);
    public Task SetVolumeAsync(float volume, CancellationToken ct) => Live.SetVolumeAsync(volume, ct);
    public Task SeekAsync(TimeSpan position, CancellationToken ct) => Live.SeekAsync(position, ct);
    public Task<EngineSnapshot> GetStateAsync(CancellationToken ct) => Live.GetStateAsync(ct);
}
