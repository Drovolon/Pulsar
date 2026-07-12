using PolyType;
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

public class StubServer : IStubHost
{
    public Task<int> GetPidAsync(CancellationToken ct) => Task.FromResult(Environment.ProcessId);
    public Task<string> EchoAsync(string message, CancellationToken ct) => Task.FromResult(message);

    public Task ExitAsync(CancellationToken ct)
    {
        // Give the RPC response a beat to flush, then die like a crashed host would.
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            Environment.Exit(1);
        });
        return Task.CompletedTask;
    }
}
