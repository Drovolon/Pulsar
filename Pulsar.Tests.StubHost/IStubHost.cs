using PolyType;
using Pulsar.Common.Api;
using StreamJsonRpc;

namespace Pulsar.Tests.StubHost;

/// <summary>Test-only RPC surface for HostConnection integration tests.</summary>
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.AllPublic)]
public partial interface IStubHost
{
    Task<int> GetPidAsync(CancellationToken ct);
    Task<string> EchoAsync(string message, CancellationToken ct);

    /// <summary>Hard-exits the host shortly after replying - deterministic "host died".</summary>
    Task ExitAsync(CancellationToken ct);
}

public class StubServer : IStubHost, IRemoteEngine
{
    public const string ExitAfterLoadPath = "stub://exit-after-load";

    private EngineSnapshot snapshot = new(
        NAudio.Wave.PlaybackState.Stopped, null, null, null, DateTimeOffset.UtcNow);

#pragma warning disable CS0067 // Required by IRemoteEngine; this crash stub never ends a track normally.
    public event EventHandler<PlaybackEnded>? OnPlaybackEnded;
#pragma warning restore CS0067
    public event EventHandler<EngineSnapshot>? OnChanged;

    public Task<int> GetPidAsync(CancellationToken ct) => Task.FromResult(Environment.ProcessId);
    public Task<string> EchoAsync(string message, CancellationToken ct) => Task.FromResult(message);

    public Task ExitAsync(CancellationToken ct)
    {
        ExitSoon();
        return Task.CompletedTask;
    }

    public Task LoadAsync(
        string path, TimeSpan position, bool startPlaying, long playbackId, CancellationToken ct)
    {
        snapshot = new EngineSnapshot(
            startPlaying ? NAudio.Wave.PlaybackState.Playing : NAudio.Wave.PlaybackState.Paused,
            path,
            null,
            new PlaybackPosition(position, TimeSpan.FromMinutes(3)),
            DateTimeOffset.UtcNow,
            playbackId);
        OnChanged?.Invoke(this, snapshot);
        if (path == ExitAfterLoadPath) ExitSoon();
        return Task.CompletedTask;
    }

    public Task LoadBytesAsync(
        string displayPath, byte[] audioData, TimeSpan position,
        bool startPlaying, long playbackId, CancellationToken ct)
        => LoadAsync(displayPath, position, startPlaying, playbackId, ct);

    public Task StopAsync(CancellationToken ct)
    {
        snapshot = snapshot with
        {
            State = NAudio.Wave.PlaybackState.Stopped,
            Path = null,
            Position = null,
            ObservedAt = DateTimeOffset.UtcNow,
        };
        OnChanged?.Invoke(this, snapshot);
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task SetVolumeAsync(float volume, CancellationToken ct) => Task.CompletedTask;
    public Task SeekAsync(TimeSpan position, CancellationToken ct) => Task.CompletedTask;
    public Task<EngineSnapshot> GetStateAsync(CancellationToken ct) => Task.FromResult(snapshot);

    private static void ExitSoon()
    {
        // Give the RPC response a beat to flush, then die like a crashed host would.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            Environment.Exit(1);
        });
    }
}
