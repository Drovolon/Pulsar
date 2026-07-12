using Pulsar.Common;
using Pulsar.Tests.StubHost;

// Spawned by HostConnection exactly like a real host: --pipe/--log-dir/--log-tag/--parent.
var hostArgs = HostArgs.Parse(args);
if (hostArgs.PipeName is null) return 2;

// Lets HostConnectionTests observe actual respawns without reaching into the
// supervisor. Only enabled explicitly by the test process.
if (Environment.GetEnvironmentVariable("STUB_START_MARKER") is { Length: > 0 } marker)
    File.AppendAllText(marker, $"{Environment.ProcessId}{Environment.NewLine}");

// A host that starts successfully but dies before opening its pipe. This lets
// HostConnectionTests prove the supervisor actually launches a replacement.
if (Environment.GetEnvironmentVariable("STUB_EXIT_BEFORE_SERVE") == "1") return 3;

// A host that spawns fine but never serves its pipe, for the
// connect-timeout/retry path.
if (Environment.GetEnvironmentVariable("STUB_NO_SERVE") == "1")
{
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}

await RpcServer.NamedPipeServerAsync<StubServer>(hostArgs.PipeName, CancellationToken.None);
return 0;
