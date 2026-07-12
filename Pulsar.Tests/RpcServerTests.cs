using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using PolyType;
using Pulsar.Common;
using StreamJsonRpc;
using Xunit;

namespace Pulsar.Tests;

[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.AllPublic)]
public partial interface IPingTarget
{
    Task<int> AddAsync(int a, int b, CancellationToken ct);
    Task<int> InstanceIdAsync(CancellationToken ct);
}

public class PingTarget : IPingTarget
{
    private static int instances;
    private readonly int id = Interlocked.Increment(ref instances);
    public Task<int> AddAsync(int a, int b, CancellationToken ct) => Task.FromResult(a + b);
    public Task<int> InstanceIdAsync(CancellationToken ct) => Task.FromResult(id);
}

/// <summary>
/// Crosses a REAL named pipe (Unix domain sockets on Linux) with the exact server
/// wiring both hosts use in production.
/// </summary>
public class RpcServerTests
{
    private sealed class Server : IAsyncDisposable
    {
        private readonly CancellationTokenSource cts = new();
        public string PipeName { get; } = $"PulsarTest.{Guid.NewGuid():N}";
        public Task Task { get; }
        public Server() => Task = RpcServer.NamedPipeServerAsync<PingTarget>(PipeName, cts.Token);

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            try { await Task.WaitAsync(TestWait.Timeout); }
            catch (OperationCanceledException) { /* the expected exit */ }
            cts.Dispose();
        }
    }

    private sealed class Client : IAsyncDisposable
    {
        public NamedPipeClientStream Pipe { get; }
        public JsonRpc Rpc { get; private set; } = null!;
        public IPingTarget Proxy { get; private set; } = null!;
        private Client(string pipeName) =>
            Pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        public static async Task<Client> ConnectAsync(string pipeName)
        {
            var c = new Client(pipeName);
            await c.Pipe.ConnectAsync(5000);
            c.Rpc = RpcServer.BuildSharedJsonRpc(c.Pipe);
            c.Proxy = c.Rpc.Attach<IPingTarget>();
            c.Rpc.StartListening();
            return c;
        }

        public async ValueTask DisposeAsync()
        {
            Rpc.Dispose();
            await Pipe.DisposeAsync();
        }
    }

    [Fact]
    public async Task Round_trips_a_call_over_a_real_pipe()
    {
        await using var server = new Server();
        await using var client = await Client.ConnectAsync(server.PipeName);
        Assert.Equal(5, await TestWait.Within(client.Proxy.AddAsync(2, 3, CancellationToken.None), "add"));
    }

    [Fact]
    public async Task Serves_a_second_client_after_the_first_disconnects()
    {
        await using var server = new Server();
        int firstId;
        await using (var first = await Client.ConnectAsync(server.PipeName))
            firstId = await TestWait.Within(first.Proxy.InstanceIdAsync(CancellationToken.None), "first id");

        await using var second = await Client.ConnectAsync(server.PipeName);
        var secondId = await TestWait.Within(second.Proxy.InstanceIdAsync(CancellationToken.None), "second id");
        Assert.NotEqual(firstId, secondId); // fresh target per connection; the loop survived the churn
    }

    [Fact]
    public async Task Serves_two_clients_concurrently()
    {
        await using var server = new Server();
        await using var a = await Client.ConnectAsync(server.PipeName);
        await using var b = await Client.ConnectAsync(server.PipeName);
        // Fire-and-forget dispatch: neither client blocks the other.
        Assert.Equal(3, await TestWait.Within(a.Proxy.AddAsync(1, 2, CancellationToken.None), "a adds"));
        Assert.Equal(7, await TestWait.Within(b.Proxy.AddAsync(3, 4, CancellationToken.None), "b adds"));
    }

    [Fact]
    public async Task Cancellation_stops_the_accept_loop_promptly()
    {
        using var cts = new CancellationTokenSource();
        var server = RpcServer.NamedPipeServerAsync<PingTarget>($"PulsarTest.{Guid.NewGuid():N}", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => server.WaitAsync(TestWait.Timeout));
    }
}
