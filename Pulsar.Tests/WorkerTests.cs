using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NAudio.Wave;
using Pulsar.Common;
using Pulsar.Common.Api;
using Xunit;
using AudioWorker = Pulsar.AudioHost.Worker;
using TranscodeWorker = Pulsar.TranscodeHost.Worker;

namespace Pulsar.Tests;

/// <summary>
/// The host workers end to end: real BackgroundService accept loops and, for the
/// audio host, a real pipe into the real AudioServer target.
/// </summary>
public class WorkerTests
{
    private static HostArgs Args(string pipe) => new() { LogDirectory = Path.GetTempPath(), PipeName = pipe };

    private sealed class CountingLogger<T> : ILogger<T>
    {
        private int errors;
        public int Errors => Volatile.Read(ref errors);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Interlocked.Increment(ref errors);
        }
    }

    [Fact]
    public async Task A_live_pipe_round_trips_into_the_real_audio_server()
    {
        var pipeName = $"PulsarTest.{Guid.NewGuid():N}";
        var worker = new AudioWorker(NullLogger<AudioWorker>.Instance, Args(pipeName));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            using var rpc = RpcServer.BuildSharedJsonRpc(pipe);
            var engine = rpc.Attach<IRemoteEngine>();
            rpc.StartListening();

            var snap = await TestWait.Within(engine.GetStateAsync(CancellationToken.None),
                "state over a real pipe into a real AudioServer");
            Assert.Equal(PlaybackState.Stopped, snap.State);
            Assert.Null(snap.Path);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task Both_workers_retry_after_a_serve_failure()
    {
        // "\0" is invalid in the pipe path on every OS: serving throws immediately,
        // forever. Two error logs prove each worker made a SECOND serving attempt;
        // merely parking forever after the first catch would not satisfy this.
        var audioLog = new CountingLogger<AudioWorker>();
        var transcodeLog = new CountingLogger<TranscodeWorker>();
        var audio = new AudioWorker(audioLog, Args("bad\0audio"));
        var transcode = new TranscodeWorker(transcodeLog, Args("bad\0transcode"));
        await audio.StartAsync(CancellationToken.None);
        await transcode.StartAsync(CancellationToken.None);
        try
        {
            await Task.WhenAll(
                TestWait.Assert(() => audioLog.Errors >= 2, "audio worker retries serving"),
                TestWait.Assert(() => transcodeLog.Errors >= 2, "transcode worker retries serving"));
            Assert.False(audio.ExecuteTask!.IsCompleted);
            Assert.False(transcode.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await audio.StopAsync(CancellationToken.None).WaitAsync(TestWait.Timeout);
            await transcode.StopAsync(CancellationToken.None).WaitAsync(TestWait.Timeout);
        }
    }

    [Fact]
    public async Task Stop_completes_promptly_while_blocked_accepting()
    {
        var worker = new AudioWorker(NullLogger<AudioWorker>.Instance, Args($"PulsarTest.{Guid.NewGuid():N}"));
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(50); // let it park in WaitForConnectionAsync
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestWait.Timeout);
    }
}
