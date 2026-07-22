using System;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Rpc;
using Pulsar.Tests.StubHost;
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

    private static string StubExePath
    {
        get
        {
            var name = OperatingSystem.IsWindows() ? "Pulsar.Tests.StubHost.exe" : "Pulsar.Tests.StubHost";
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "stubhost", name);
            Assert.True(System.IO.File.Exists(path), $"stub host missing at {path}");
            if (!OperatingSystem.IsWindows())
                System.IO.File.SetUnixFileMode(
                    path,
                    System.IO.File.GetUnixFileMode(path) | System.IO.UnixFileMode.UserExecute);
            return path;
        }
    }

    private static HostSpec LiveSpec => new(
        ExePath: StubExePath,
        PipeName: $"pulsar-engine-test-{Guid.NewGuid():N}",
        LogDirectory: System.IO.Path.GetTempPath(),
        LogTag: "engine-test");

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
            () => engine.LoadAsync(
                "x.opus", TimeSpan.Zero, true, 1, CancellationToken.None)).WaitAsync(TestWait.Timeout);
    }

    [Fact]
    public async Task Dispose_before_start_is_safe()
    {
        var engine = new ReconnectingEngine(Spec);
        await engine.DisposeAsync().AsTask().WaitAsync(TestWait.Timeout); // must not hang or throw
    }

    [Fact]
    public async Task Host_death_reports_the_active_playback_id_as_disconnected()
    {
        await using var engine = new ReconnectingEngine(LiveSpec);
        var connections = 0;
        var ended = new TaskCompletionSource<Pulsar.Common.Api.PlaybackEnded>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnReconnected += () => Interlocked.Increment(ref connections);
        engine.OnPlaybackEnded += (_, value) => ended.TrySetResult(value);
        engine.Start();
        await TestWait.Assert(() => Volatile.Read(ref connections) == 1, "initial engine connection");

        await engine.LoadAsync(
            StubServer.ExitAfterLoadPath,
            TimeSpan.Zero,
            true,
            42,
            CancellationToken.None);

        var disconnected = await TestWait.Within(ended.Task, "disconnect event");
        Assert.Equal(42, disconnected.PlaybackId);
        Assert.Equal(Pulsar.Common.Api.EndReason.Disconnected, disconnected.Reason);
        await TestWait.Assert(() => Volatile.Read(ref connections) == 2, "engine reconnects");
    }
}
