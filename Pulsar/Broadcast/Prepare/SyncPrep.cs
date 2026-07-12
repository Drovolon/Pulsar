using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Pulsar.Common.Api;
using StreamJsonRpc;

namespace Pulsar.Broadcast.Prepare;

public abstract record PrepResult
{
    public sealed record Successful(string PreparedFilePath, double GainDb) : PrepResult;

    public sealed record Failed(Exception Ex) : PrepResult;

    public sealed record Preempted : PrepResult;
}

/// <summary>
/// SyncPrep is responsible for the transcode and ReplayGain calculation for tracks
/// before they are synced. (Prep for sync - SyncPrep.)
///
/// Uses actor model. It accepts prep requests and manages a worker thread that does
/// the actual prep. Only one track can be prepped at a time (since transcoding can
/// be CPU intensive). This is used both for current-active and prefetch preps;
/// and, a new active prep request will preempt any ongoing prefetch prep.
///
/// Prefetch preemption is important because sync can't happen until prep finishes.
/// So if a new active request comes in, we're holding up shipping the prepped file
/// to everyone else.
/// </summary>
public class SyncPrep : IAsyncDisposable
{
    private enum PrepPriority { Prefetch, Active }

    private sealed record Pending(string FilePath, TaskCompletionSource<PrepResult> Tcs);

    private sealed class RunningJob(Pending pending, PrepPriority priority)
    {
        internal string FilePath { get; } = pending.FilePath;
        internal PrepPriority Priority { get; set; } = priority;
        internal CancellationTokenSource Cts { get; } = new();
        internal List<TaskCompletionSource<PrepResult>> Waiters { get; } = [pending.Tcs];
    }
    
    private readonly Lock @lock = new();
    
    private readonly Task loop;
    private readonly CancellationTokenSource loopCts = new();

    // Currently running transcode. New prep request for the same path gets given that
    // task, instead of canceling and spawning a new one.
    private RunningJob? running;

    // Last successful prepped file. Skipped during cache deletion.
    private string? lastActiveArtifact;

    private Pending? activeSlot;
    private Pending? prefetchSlot;

    private bool disposed; // guarded by @lock: enqueueing after dispose would hang the caller
    
    // A wake-up is only a hint to inspect the two slots, so one buffered signal is enough.
    // Job completion re-signals if another slot remains populated.
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    
    /// <summary>
    /// Holds original file path -> the latest observable result for BroadcastManager.TryGet.
    /// A successful result with a live artifact also serves later requests immediately;
    /// failures remain observable but do not prevent a retry. Preemptions are never recorded.
    /// Note, this isn't persisted anywhere, so everything will get re-transcoded
    /// on next plugin load. IMO, it's not appropriate to keep a huge cache on disk
    /// for this plugin - I considered setting it to like 1G, persisting these results...
    /// but that's probably not enough for a big music library anyway (it's like ~150 songs).
    /// </summary>
    private readonly ConcurrentDictionary<string, PrepResult> observedResults = new();
    
    private readonly CacheManager cacheManager;
    private readonly IPrepareService prepareService;

    public SyncPrep(CacheManager cacheManager, IPrepareService prepareService)
    {
        this.cacheManager = cacheManager;
        this.prepareService = prepareService;
        loop = Task.Run(WorkerLoop);
    }

    public Task<PrepResult> PrepareActive(string filePath) => Prep(filePath, PrepPriority.Active);

    public Task<PrepResult> PreparePrefetch(string filePath) => Prep(filePath, PrepPriority.Prefetch);

    public bool TryGet(string key, out PrepResult? result) => observedResults.TryGetValue(key, out result);

    private Task<PrepResult> Prep(string filePath, PrepPriority priority)
    {
        lock (@lock)
        {
            if (disposed)
                return Task.FromResult<PrepResult>(new PrepResult.Preempted());

            if (TryGetPrepared(filePath, out var success))
            {
                ApplySupersession(filePath, priority);
                if (priority == PrepPriority.Active) lastActiveArtifact = success.PreparedFilePath;
                CacheManager.Touch(success.PreparedFilePath);
                return Task.FromResult<PrepResult>(success);
            }

            var tcs = NewCompletion();
            if (running is { } current
                && current.FilePath == filePath
                && !current.Cts.IsCancellationRequested)
            {
                current.Waiters.Add(tcs);
                if (priority == PrepPriority.Active) current.Priority = PrepPriority.Active;
                return tcs.Task;
            }

            ApplySupersession(filePath, priority);
            Queue(new Pending(filePath, tcs), priority);
            SignalWorker();
            return tcs.Task;
        }
    }

    private static TaskCompletionSource<PrepResult> NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool TryGetPrepared(string filePath, out PrepResult.Successful success)
    {
        if (observedResults.TryGetValue(filePath, out var known)
            && known is PrepResult.Successful prepared
            && File.Exists(prepared.PreparedFilePath))
        {
            success = prepared;
            return true;
        }
        success = null!;
        return false;
    }

    // Assumes @lock held. The latest request of a priority supersedes stale work of
    // that priority; active work may also preempt a different running prefetch.
    private void ApplySupersession(string filePath, PrepPriority priority)
    {
        if (priority == PrepPriority.Active)
        {
            Preempt(ref activeSlot);
            if (prefetchSlot?.FilePath == filePath) Preempt(ref prefetchSlot);
            if (running is { } job && job.FilePath != filePath) job.Cts.Cancel();
        }
        else
        {
            Preempt(ref prefetchSlot);
            if (running is { Priority: PrepPriority.Prefetch } job && job.FilePath != filePath)
                job.Cts.Cancel();
        }
    }

    private static void Preempt(ref Pending? pending)
    {
        pending?.Tcs.TrySetResult(new PrepResult.Preempted());
        pending = null;
    }

    private void Queue(Pending pending, PrepPriority priority)
    {
        if (priority == PrepPriority.Active) activeSlot = pending;
        else prefetchSlot = pending;
    }

    private void SignalWorker() => wake.Writer.TryWrite(true);
    
    private async Task WorkerLoop()
    {
        while (!loopCts.IsCancellationRequested)
        {
            try
            {
                await wake.Reader.ReadAsync(loopCts.Token);
            }
            catch (OperationCanceledException) when (loopCts.IsCancellationRequested)
            {
                break;
            }

            RunningJob job;
            lock (@lock)
            {
                if (activeSlot is { } a)
                {
                    job = new RunningJob(a, PrepPriority.Active);
                    activeSlot = null;
                }
                else if (prefetchSlot is { } p)
                {
                    job = new RunningJob(p, PrepPriority.Prefetch);
                    prefetchSlot = null;
                }
                else continue;

                running = job;
            }

            PrepResult result;
            try
            {
                var outPath = cacheManager.CachePathFor(job.FilePath);
                var processed =
                    await prepareService.PrepareFileAsync(job.FilePath, outPath, job.Cts.Token);
                result = new PrepResult.Successful(processed.SyncPath, processed.GainDb);
            }
            catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
            {
                result = new PrepResult.Preempted();
            }
            catch (Exception ex)
            {
                result = new PrepResult.Failed(ex);
            }

            List<TaskCompletionSource<PrepResult>> waiters;
            lock (@lock)
            {
                switch (result)
                {
                    case PrepResult.Successful ok:
                        if (job.Priority == PrepPriority.Active) lastActiveArtifact = ok.PreparedFilePath;
                        cacheManager.TryEvictLru(ok.PreparedFilePath, lastActiveArtifact);
                        observedResults[job.FilePath] = result;
                        break;
                    case PrepResult.Failed { Ex: not ConnectionLostException }:
                        observedResults[job.FilePath] = result;
                        break;
                }
                job.Cts.Dispose();
                running = null;
                waiters = job.Waiters;
                if (activeSlot is not null || prefetchSlot is not null) SignalWorker();
            }
            foreach (var waiter in waiters) waiter.TrySetResult(result);
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        lock (@lock)
        {
            disposed = true;
            running?.Cts.Cancel();
            Preempt(ref activeSlot);
            Preempt(ref prefetchSlot);
        }
        loopCts.Cancel();
        await loop;
    }
}
