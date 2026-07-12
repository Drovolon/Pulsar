using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Rpc;
using StreamJsonRpc;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// Same contract ReconnectingEngineTests covers for the audio side: while the
/// transcode host is down, calls fail with ConnectionLostException (the shape
/// SyncPrep already tolerates), and teardown is safe at any lifecycle point.
/// </summary>
public class ReconnectingPrepareServiceTests
{
    private static HostSpec Spec => new(
        ExePath: "/nonexistent/Pulsar.TranscodeHost.exe",
        PipeName: $"pulsar-test-{Guid.NewGuid():N}",
        LogDirectory: Path.GetTempPath(),
        LogTag: "test");

    // Both tests bound their awaits: a regression that HANGS (rather than throws)
    // must fail the test, not wedge the whole run.
    [Fact]
    public async Task Calls_while_disconnected_throw_connection_lost()
    {
        await using var prep = new ReconnectingPrepareService(Spec); // never Start()ed

        await Assert.ThrowsAsync<ConnectionLostException>(
            () => prep.PrepareAsync("in.flac", "out", CancellationToken.None)).WaitAsync(TestWait.Timeout);
        await Assert.ThrowsAsync<ConnectionLostException>(
            () => prep.PrepareBytesAsync("in.scd", [1], "out", CancellationToken.None)).WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task Dispose_before_start_is_safe()
    {
        var prep = new ReconnectingPrepareService(Spec);
        await prep.DisposeAsync().AsTask().WaitAsync(TestWait.Timeout); // must not hang or throw
    }
}
