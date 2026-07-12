using System;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Rpc;
using StreamJsonRpc;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// The facade contract consumers rely on: while the host is down, calls fail with
/// ConnectionLostException (the failure shape ListeningManager/SyncPrep already
/// tolerate), and teardown is safe at any point in the lifecycle.
/// </summary>
public class ReconnectingEngineTests
{
    private static HostSpec Spec => new(
        ExePath: "/nonexistent/Pulsar.AudioHost.exe",
        PipeName: $"pulsar-test-{Guid.NewGuid():N}",
        LogDirectory: System.IO.Path.GetTempPath(),
        LogTag: "test");

    // Both tests bound their awaits: a regression that HANGS (rather than throws)
    // must fail the test, not wedge the whole run.
    [Fact]
    public async Task Calls_while_disconnected_throw_connection_lost()
    {
        await using var engine = new ReconnectingEngine(Spec); // never Start()ed

        await Assert.ThrowsAsync<ConnectionLostException>(
            () => engine.StopAsync(CancellationToken.None)).WaitAsync(TestWait.Timeout);
        await Assert.ThrowsAsync<ConnectionLostException>(
            () => engine.GetStateAsync(CancellationToken.None)).WaitAsync(TestWait.Timeout);
        await Assert.ThrowsAsync<ConnectionLostException>(
            () => engine.LoadAsync("x.opus", TimeSpan.Zero, true, CancellationToken.None)).WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task Dispose_before_start_is_safe()
    {
        var engine = new ReconnectingEngine(Spec);
        await engine.DisposeAsync().AsTask().WaitAsync(TestWait.Timeout); // must not hang or throw
    }
}
