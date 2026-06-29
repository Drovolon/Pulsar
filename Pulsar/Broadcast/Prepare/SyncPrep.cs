using System;
using System.Collections.Concurrent;
using System.Threading;
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
    private sealed record Pending(string FilePath, TaskCompletionSource<PrepResult> Tcs);
    
    private readonly Lock @lock = new();
    
    private readonly Task loop;
    private readonly CancellationTokenSource loopCts = new();
    private CancellationTokenSource? runningCts;

    private bool runningIsActive;
    
    private Pending? activeSlot;
    private Pending? prefetchSlot;
    
    private readonly SemaphoreSlim signal = new(0);
    
    /// <summary>
    /// Holds original file path -> results. Only caches success/fail, not preempted.
    /// Note, this isn't persisted anywhere, so everything will get re-transcoded
    /// on next plugin load. IMO, it's not appropriate to keep a huge cache on disk
    /// for this plugin - I considered setting it to like 1G, persisting these results...
    /// but that's probably not enough for a big music library anyway (it's like ~150 songs).
    /// </summary>
    private readonly ConcurrentDictionary<string, PrepResult> results = new();
    
    private readonly CacheManager cacheManager;
    private readonly IPrepareService prepareService;

    public SyncPrep(CacheManager cacheManager, IPrepareService prepareService)
    {
        this.cacheManager = cacheManager;
        this.prepareService = prepareService;
        loop = Task.Run(WorkerLoop);
    }

    public Task<PrepResult> PrepareActive(string filePath)
    {
        return Prep(filePath, true);
    }

    public Task<PrepResult> PreparePrefetch(string filePath)
    {
        return Prep(filePath, false);
    }

    public bool TryGet(string key, out PrepResult? result)
    {
        return results.TryGetValue(key, out result);
    }

    private Task<PrepResult> Prep(string filePath, bool isActive)
    {
        var tcs = new TaskCompletionSource<PrepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (@lock)
        {
            if (isActive)
            {
                activeSlot?.Tcs.TrySetResult(new PrepResult.Preempted());
                if (prefetchSlot?.FilePath == filePath)
                {
                    // promote prefetch to active if it matches
                    activeSlot = prefetchSlot;
                    prefetchSlot = null;
                    // if active was previously running: cancel it, we want
                    if (runningIsActive)
                        runningCts?.Cancel();
                }
                else
                {
                    activeSlot = new Pending(filePath, tcs);
                    runningCts?.Cancel();
                    if (!runningIsActive)
                        runningCts?.Cancel();
                }
            }
            else
            {
                prefetchSlot?.Tcs.TrySetResult(new PrepResult.Preempted());
                prefetchSlot = new Pending(filePath, tcs);
                if (!runningIsActive)
                    runningCts?.Cancel();
            }
        }

        signal.Release();
        return tcs.Task;
    }
    
    private async Task WorkerLoop()
    {
        while (!loopCts.IsCancellationRequested)
        {
            try
            {
                await signal.WaitAsync(loopCts.Token);
            }
            catch (OperationCanceledException) when (loopCts.IsCancellationRequested)
            {
                break;
            }

            Pending job;
            lock (@lock)
            {
                if (activeSlot is { } a)
                {
                    job = a;
                    activeSlot = null;
                    runningIsActive = true;
                }
                else if (prefetchSlot is { } p)
                {
                    job = p;
                    prefetchSlot = null;
                    runningIsActive = false;
                }
                else continue;

                runningCts = new CancellationTokenSource();
            }

            PrepResult result;
            try
            {
                var outPath = cacheManager.CachePathFor(job.FilePath);
                var processed =
                    await prepareService.PrepareFileAsync(job.FilePath, outPath, runningCts.Token);
                result = new PrepResult.Successful(processed.SyncPath, processed.GainDb);
                cacheManager.TryEvictLru(processed.SyncPath);
                results.TryAdd(job.FilePath, result);
            }
            catch (OperationCanceledException) when (runningCts.IsCancellationRequested)
            {
                result = new PrepResult.Preempted();
            }
            catch (Exception ex)
            {
                result = new PrepResult.Failed(ex);
                if (ex is not ConnectionLostException) // don't cache failure if the transcode host is dead
                    results.TryAdd(job.FilePath, result);
            }

            lock (@lock)
            {
                runningCts.Dispose();
                runningCts = null;
            }
            job.Tcs.TrySetResult(result);
        }
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        runningCts?.Cancel();
        loopCts.Cancel();
        await loop;
    }
}
