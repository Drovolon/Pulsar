using System;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Common.Api;
using StreamJsonRpc;

namespace Pulsar.Rpc;

/// <summary>
/// IPrepareService for use in the rest of the plugin. Throws ConnectionLostException while
/// the connection is down.
/// </summary>
public sealed class ReconnectingPrepareService(HostSpec spec) : IPrepareService, IAsyncDisposable
{
    private readonly HostConnection<IPrepareService> connection = new(spec, _ => { });

    public void Start() => connection.Start();

    public ValueTask DisposeAsync() => connection.DisposeAsync();

    private IPrepareService Live => connection.Proxy
        ?? throw new ConnectionLostException($"{connection.HostName} is not connected");

    public Task<PreparedTrack> PrepareAsync(string originalPath, string transcodeOutPath, CancellationToken ct)
        => Live.PrepareAsync(originalPath, transcodeOutPath, ct);

    public Task<PreparedTrack> PrepareBytesAsync(string originalPath, byte[] audioData, string transcodeOutPath, CancellationToken ct)
        => Live.PrepareBytesAsync(originalPath, audioData, transcodeOutPath, ct);
}
