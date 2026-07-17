using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Rpc;
using Pulsar.Tests.StubHost;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// The supervisor loop against a REAL subprocess serving a REAL pipe with the
/// production RpcServer wiring: spawn, connect, RPC, death, backoff, respawn,
/// dispose-kills. Fast test timings; prod defaults untouched.
/// </summary>
public class HostConnectionTests : IAsyncLifetime
{
    private static readonly TimeSpan FastConnect = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FastBackoff = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan FastCap = TimeSpan.FromMilliseconds(100);

    private readonly List<int> pids = [];
    private readonly List<string> markerFiles = [];
    private HostConnection<IStubHost>? connection;

    private static string StubExePath
    {
        get
        {
            var name = OperatingSystem.IsWindows() ? "Pulsar.Tests.StubHost.exe" : "Pulsar.Tests.StubHost";
            var path = Path.Combine(AppContext.BaseDirectory, "stubhost", name);
            Assert.True(File.Exists(path), $"stub host missing at {path} - did the CopyStubHost target run?");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
            return path;
        }
    }

    private static HostSpec Spec(IReadOnlyDictionary<string, string>? env = null) => new(
        ExePath: StubExePath,
        PipeName: $"PulsarTest.{Guid.NewGuid():N}",
        LogDirectory: Path.GetTempPath(),
        LogTag: "stub",
        Environment: env);

    private HostConnection<IStubHost> Create(HostSpec spec)
        => connection = new HostConnection<IStubHost>(spec, _ => { }, FastConnect, FastBackoff, FastCap);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (connection is not null) await connection.DisposeAsync();
        foreach (var pid in pids)
        {
            try { Process.GetProcessById(pid).Kill(); }
            catch { /* already dead - the expected case */ }
        }
        foreach (var marker in markerFiles)
        {
            try { File.Delete(marker); }
            catch { /* best-effort test artifact cleanup */ }
        }
    }

    private string CreateStartMarker()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pulsar-stub-starts-{Guid.NewGuid():N}.txt");
        markerFiles.Add(path);
        return path;
    }

    private static int[] StartedPids(string marker)
    {
        try
        {
            return File.Exists(marker)
                ? File.ReadAllLines(marker).Select(int.Parse).ToArray()
                : [];
        }
        catch (IOException)
        {
            return []; // the child may be in the middle of appending its line
        }
    }

    private async Task<int> WaitConnected(HostConnection<IStubHost> conn)
    {
        await TestWait.Assert(() => conn.Proxy is not null, "connected to the stub host");
        var pid = await TestWait.Within(conn.Proxy!.GetPidAsync(CancellationToken.None), "pid");
        pids.Add(pid);
        return pid;
    }

    [Fact]
    public async Task Spawns_connects_and_round_trips()
    {
        var connected = 0;
        var conn = Create(Spec());
        conn.OnConnected += () => Interlocked.Increment(ref connected);
        conn.Start();

        await WaitConnected(conn);
        Assert.Equal("hello", await TestWait.Within(
            conn.Proxy!.EchoAsync("hello", CancellationToken.None), "echo"));
        Assert.Equal(1, connected);
    }

    [Fact]
    public async Task A_dying_host_is_respawned_and_reconnected()
    {
        var disconnects = 0;
        var conn = Create(Spec());
        conn.OnDisconnected += () => Interlocked.Increment(ref disconnects);
        conn.Start();
        var pid1 = await WaitConnected(conn);

        await TestWait.Within(conn.Proxy!.ExitAsync(CancellationToken.None), "exit request");

        await TestWait.Assert(() => disconnects == 1, "death observed exactly once");
        var pid2 = await WaitConnected(conn);
        Assert.NotEqual(pid1, pid2);      // a FRESH process, not the corpse
        Assert.Equal(1, disconnects);     // and no double-fire across the reconnect
    }

    [Fact]
    public async Task Repeated_host_deaths_each_respawn_a_fresh_working_process()
    {
        var connected = 0;
        var disconnects = 0;
        var conn = Create(Spec());
        conn.OnConnected += () => Interlocked.Increment(ref connected);
        conn.OnDisconnected += () => Interlocked.Increment(ref disconnects);
        conn.Start();

        var seenPids = new HashSet<int> { await WaitConnected(conn) };
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            await TestWait.Within(conn.Proxy!.ExitAsync(CancellationToken.None), $"exit request #{cycle}");
            await TestWait.Assert(() => disconnects == cycle, $"death #{cycle} observed exactly once");

            var nextPid = await WaitConnected(conn);
            Assert.True(seenPids.Add(nextPid), $"cycle #{cycle} spawned a fresh PID");
            Assert.Equal($"cycle-{cycle}", await TestWait.Within(
                conn.Proxy!.EchoAsync($"cycle-{cycle}", CancellationToken.None),
                $"echo after reconnect #{cycle}"));
        }

        await TestWait.Assert(() => connected == 4, "all four connection events fired");
        Assert.Equal(3, disconnects);
    }

    [Fact]
    public async Task A_missing_executable_stays_disconnected_and_disposes_cleanly()
    {
        var conn = connection = new HostConnection<IStubHost>(
            Spec() with { ExePath = "/nonexistent/stub-host" }, _ => { },
            FastConnect, FastBackoff, FastCap);
        conn.Start();

        await Task.Delay(200); // give the supervisor time to encounter the launch failure
        Assert.Null(conn.Proxy);
        await conn.DisposeAsync().AsTask().WaitAsync(TestWait.Timeout);
        connection = null;
    }

    [Fact]
    public async Task A_host_that_exits_before_serving_is_restarted()
    {
        var marker = CreateStartMarker();
        var conn = Create(Spec(new Dictionary<string, string>
        {
            ["STUB_EXIT_BEFORE_SERVE"] = "1",
            ["STUB_START_MARKER"] = marker,
        }));
        conn.Start();

        await TestWait.Assert(() => StartedPids(marker).Distinct().Count() >= 2,
            "a fresh process is started after the previous host exits");
        pids.AddRange(StartedPids(marker));
        Assert.Null(conn.Proxy);
        await conn.DisposeAsync().AsTask().WaitAsync(TestWait.Timeout);
        connection = null;
    }

    [Fact]
    public async Task A_host_that_never_serves_stays_disconnected_and_disposes_cleanly()
    {
        var conn = Create(Spec(new Dictionary<string, string> { ["STUB_NO_SERVE"] = "1" }));
        conn.Start();

        await Task.Delay(300); // spawn + a connection timeout
        Assert.Null(conn.Proxy);
        await conn.DisposeAsync().AsTask().WaitAsync(TestWait.Timeout);
        connection = null;
    }

    [Fact]
    public async Task Dispose_kills_the_host_and_suppresses_the_disconnect_event()
    {
        var disconnects = 0;
        var conn = Create(Spec());
        conn.OnDisconnected += () => Interlocked.Increment(ref disconnects);
        conn.Start();
        var pid = await WaitConnected(conn);

        await conn.DisposeAsync().AsTask().WaitAsync(TestWait.Timeout);
        connection = null;

        Assert.Equal(0, disconnects); // cancellation suppresses OnDisconnected
        await TestWait.Assert(() =>
        {
            try { return Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { return true; }
        }, "dispose killed the stub process");
    }

    [Fact]
    public async Task A_throwing_subscriber_cannot_kill_the_supervisor()
    {
        var conn = Create(Spec());
        conn.OnConnected += () => throw new InvalidOperationException("rude subscriber");
        conn.Start();

        await WaitConnected(conn); // RaiseSafely contains it; connection completes anyway
        Assert.Equal("ok", await TestWait.Within(
            conn.Proxy!.EchoAsync("ok", CancellationToken.None), "echo after rude subscriber"));
    }
}
