using Pulsar.AudioHost.Playback;
using Pulsar.Common.Api;

namespace Pulsar.AudioHost;

/// <summary>
/// Implementation of the IRemoteEngine JSON-RPC API.
///
/// Note, the weird usage of Task for these simple methods is to allow the client to use
/// the strongly-typed RPC invocation method in JSON-RPC, while also enforcing that the server
/// uses the same construct.
///
/// See https://microsoft.github.io/vs-streamjsonrpc/docs/proxies.html#server-side-concerns
/// </summary>
public class AudioServer : IRemoteEngine, IAsyncDisposable
{
    private readonly FilePlayer filePlayer = new();
    
    public AudioServer()
    {
        // Load-bearing lambdas: `+= OnChanged` captures the value of OnChanged in the ctor (null).
        // Re-reading OnChanged on every invocation means later-set handlers are correctly invoked.
        filePlayer.OnChanged += (_, snapshot) => OnChanged?.Invoke(this, snapshot);
        filePlayer.OnPlaybackEnded += (_, reason) => OnPlaybackEnded?.Invoke(this, reason);
    }
    
    public Task LoadAsync(
        string path, TimeSpan position, bool startPlaying, long playbackId, CancellationToken ct)
    {
        filePlayer.Load(path, position, startPlaying, playbackId);
        return Task.CompletedTask;
    }

    public Task LoadBytesAsync(
        string displayPath, byte[] audioData, TimeSpan position,
        bool startPlaying, long playbackId, CancellationToken ct)
    {
        filePlayer.LoadBytes(displayPath, audioData, position, startPlaying, playbackId);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        filePlayer.Stop();
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct)
    {
        filePlayer.Pause();
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct)
    {
        filePlayer.Resume();
        return Task.CompletedTask;
    }

    public Task SetVolumeAsync(float volume, CancellationToken ct)
    {
        filePlayer.Volume(volume);
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position, CancellationToken ct)
    {
        filePlayer.Seek(position);
        return Task.CompletedTask;
    }

    public Task<EngineSnapshot> GetStateAsync(CancellationToken ct)
    {
        return Task.FromResult(filePlayer.Snapshot with
        {
            ObservedAt = DateTimeOffset.UtcNow,
        });
    }

    public event EventHandler<PlaybackEnded>? OnPlaybackEnded;
    public event EventHandler<EngineSnapshot>? OnChanged;

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        await filePlayer.DisposeAsync();
    }
}
