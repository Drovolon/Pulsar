using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Common;
using StreamJsonRpc;

namespace Pulsar.Rpc;

/// <summary>
/// Fully owns a subprocess, the NamedPipe to connect to it, the JsonRpcChannel, and the proxy.
/// A supervisor loop handles recreation and reconnecting when any failure happens.
/// The host subprocesses handle the game dying; DisposeAsync handles the plugin unloading gracefully;
/// this class handles the rest.
/// </summary>
public sealed class HostConnection<T> : IAsyncDisposable
    where T : class
{
    private readonly HostSpec spec;
    private readonly Action<T> wireProxy;
    private readonly TimeSpan connectTimeout;
    private readonly TimeSpan backoffUnit;
    private readonly TimeSpan maxBackoff;

    private readonly CancellationTokenSource cts = new();
    private Task? supervisor;

    private Process? process;
    private volatile T? proxy;

    public HostConnection(HostSpec spec, Action<T> wireProxy)
        : this(spec, wireProxy,
               connectTimeout: TimeSpan.FromSeconds(10),
               backoffUnit: TimeSpan.FromSeconds(1),
               maxBackoff: TimeSpan.FromSeconds(30)) { }

    // For unit tests
    internal HostConnection(HostSpec spec, Action<T> wireProxy,
        TimeSpan connectTimeout, TimeSpan backoffUnit, TimeSpan maxBackoff)
    {
        this.spec = spec;
        this.wireProxy = wireProxy;
        this.connectTimeout = connectTimeout;
        this.backoffUnit = backoffUnit;
        this.maxBackoff = maxBackoff;
        HostName = spec.PipeName;
    }

    /// <summary>Exponential backoff: unit * 2^(attempt-1), with a ceiling.</summary>
    internal static TimeSpan Backoff(int attempt, TimeSpan unit, TimeSpan ceiling)
    {
        var factor = Math.Min(Math.Pow(2, attempt - 1), ceiling / unit);
        return unit * factor;
    }

    public string HostName { get; }

    /// <summary>The live proxy, or null while disconnected.</summary>
    public T? Proxy => proxy;

    public event Action? OnConnected;

    public event Action? OnDisconnected;

    public void Start()
    {
        Directory.CreateDirectory(spec.LogDirectory);
        supervisor = Task.Run(SupervisorLoop);
    }

    private async Task SupervisorLoop()
    {
        var ct = cts.Token;
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            JsonRpc? rpc = null;
            try
            {
                EnsureProcess();

                pipe = new NamedPipeClientStream(".", spec.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, ct);

                rpc = RpcServer.BuildSharedJsonRpc(pipe);
                var fresh = rpc.Attach<T>();
                wireProxy(fresh);
                rpc.StartListening();

                proxy = fresh;
                attempt = 0;
                Plugin.Log.Information("Connected to {host}", HostName);
                RaiseSafely(OnConnected, nameof(OnConnected));

                await AwaitDeath(rpc, ct); // this is such a metal name, I couldn't resist
                if (ct.IsCancellationRequested) break;

                Plugin.Log.Warning("Lost connection to {host}: {reason}", HostName,
                    rpc.Completion.Exception?.GetBaseException().Message ?? "connection closed");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning("Connecting to {host} failed: {message}", HostName, ex.Message);
            }
            finally
            {
                if (proxy is not null)
                {
                    proxy = null;
                    if (!ct.IsCancellationRequested)
                        RaiseSafely(OnDisconnected, nameof(OnDisconnected));
                }
                rpc?.Dispose();
                pipe?.Dispose();
            }

            attempt++;
            var delay = Backoff(attempt, backoffUnit, maxBackoff);
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task AwaitDeath(JsonRpc rpc, CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reg = ct.Register(() => cancelled.TrySetResult());
        await Task.WhenAny(rpc.Completion, cancelled.Task);
    }

    private void RaiseSafely(Action? handler, string name)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "{host} {name} handler threw", HostName, name);
        }
    }

    private void EnsureProcess()
    {
        if (process is { HasExited: false }) return;

        process?.Dispose();
        process = null;

        if (!File.Exists(spec.ExePath))
            throw new FileNotFoundException($"Host executable missing: {spec.ExePath}", spec.ExePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = spec.ExePath,
            WorkingDirectory = Path.GetDirectoryName(spec.ExePath)!,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "--pipe", spec.PipeName,
                "--log-dir", spec.LogDirectory,
                "--log-tag", spec.LogTag,
                "--parent", Environment.ProcessId.ToString(),
            },
        };

        if (spec.MixerName is not null)
        {
            startInfo.ArgumentList.Add("--mixer-name");
            startInfo.ArgumentList.Add(spec.MixerName);
        }

        if (spec.Environment is not null)
        {
            foreach (var (key, value) in spec.Environment)
                startInfo.Environment[key] = value;
        }

        var fresh = new Process { StartInfo = startInfo };
        try
        {
            if (!fresh.Start())
                throw new InvalidOperationException($"Failed to start host process: {spec.ExePath}");
        }
        catch
        {
            fresh.Dispose();
            throw;
        }

        // Forward stdout/stderr to Dalamud logs
        // (Actual logs use Serilog to disk)
        _ = DrainToPluginLogAsync(fresh.StandardOutput, HostName);
        _ = DrainToPluginLogAsync(fresh.StandardError, HostName);

        Plugin.Log.Information("Started {name} (pid {pid})", HostName, fresh.Id);
        process = fresh;
    }

    private static async Task DrainToPluginLogAsync(StreamReader reader, string name)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
                Plugin.Log.Debug("[{name}] {line}", name, line);
        }
        catch
        {
            // stream closed with the process
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        if (supervisor is not null)
        {
            try
            {
                await supervisor;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Error during dispose for {host} supervisor", HostName);
            }
        }

        if (process is not null)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
            catch
            {
                // already exited
            }
            finally
            {
                process.Dispose();
            }
        }

        cts.Dispose();
    }
}
