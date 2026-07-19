using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Common.Api;
using Pulsar.Concurrency;
using StreamJsonRpc;

namespace Pulsar.Broadcast.Prepare;

public abstract record PrepResult
{
    public sealed record Successful(
        string PreparedFilePath, string Blake3Hash, string Sha1Hash, double GainDb) : PrepResult;

    public sealed record Failed(Exception Ex) : PrepResult;

    public sealed record Preempted : PrepResult;
}

/// <summary>
/// SyncPrep is responsible for the transcode and ReplayGain calculation for tracks
/// before they are synced. (Prep for sync - SyncPrep.)
///
/// Uses the actor model. The mailbox schedules at most one external prep job at a time
/// (since transcoding can be CPU intensive), while remaining responsive to requests that
/// cancel or supersede that job. This is used for both active and prefetch preparation;
/// a new active request preempts any ongoing prefetch.
///
/// Prefetch preemption is important because sync can't happen until prep finishes.
/// So if a new active request comes in, we're holding up shipping the prepped file
/// to everyone else.
/// </summary>
public class SyncPrep : IAsyncDisposable
{
    private enum PrepPriority { Prefetch, Active }

    private abstract record Message;
    private sealed record PrepRequested(
        string FilePath,
        PrepPriority Priority,
        TaskCompletionSource<PrepResult> Completion) : Message;
    private sealed record JobCompleted(RunningJob Job, PrepResult Result) : Message;

    private sealed record Pending(string FilePath, TaskCompletionSource<PrepResult> Tcs);

    private sealed class RunningJob(Pending pending, PrepPriority priority)
    {
        internal string FilePath { get; } = pending.FilePath;
        internal PrepPriority Priority { get; set; } = priority;
        internal CancellationTokenSource Cts { get; } = new();
        internal List<TaskCompletionSource<PrepResult>> Waiters { get; } = [pending.Tcs];
    }

    private readonly SerializedMailbox<Message> mailbox;

    // Currently running transcode. New prep request for the same path gets given that
    // task, instead of canceling and spawning a new one.
    private RunningJob? running;
    private Task? runningTask;

    // Last successful prepped file. Skipped during cache deletion.
    private string? lastActiveArtifact;

    private Pending? activeSlot;
    private Pending? prefetchSlot;

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
        mailbox = new SerializedMailbox<Message>(HandleMessage, OnMessageError, OnCompleted);
    }

    public Task<PrepResult> PrepareActive(string filePath) => Prep(filePath, PrepPriority.Active);

    public Task<PrepResult> PreparePrefetch(string filePath) => Prep(filePath, PrepPriority.Prefetch);

    public bool TryGet(string key, out PrepResult? result) => observedResults.TryGetValue(key, out result);

    private Task<PrepResult> Prep(string filePath, PrepPriority priority)
    {
        var completion = NewCompletion();
        if (!mailbox.TryPost(new PrepRequested(filePath, priority, completion)))
            completion.TrySetResult(new PrepResult.Preempted());
        return completion.Task;
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

    private ValueTask HandleMessage(Message message)
    {
        switch (message)
        {
            case PrepRequested(var filePath, var priority, var completion):
                HandleRequest(filePath, priority, completion);
                break;
            case JobCompleted(var job, var result):
                HandleCompleted(job, result);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void HandleRequest(
        string filePath,
        PrepPriority priority,
        TaskCompletionSource<PrepResult> completion)
    {
        if (TryGetPrepared(filePath, out var success))
        {
            ApplySupersession(filePath, priority);
            if (priority == PrepPriority.Active) lastActiveArtifact = success.PreparedFilePath;
            CacheManager.Touch(success.PreparedFilePath);
            completion.TrySetResult(success);
            StartNextIfIdle();
            return;
        }

        if (running is { } current
            && current.FilePath == filePath
            && !current.Cts.IsCancellationRequested)
        {
            current.Waiters.Add(completion);
            if (priority == PrepPriority.Active) current.Priority = PrepPriority.Active;
            return;
        }

        ApplySupersession(filePath, priority);
        Queue(new Pending(filePath, completion), priority);
        StartNextIfIdle();
    }

    // The latest request of a priority supersedes stale work of that priority;
    // active work may also preempt a different running prefetch.
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

    private void StartNextIfIdle()
    {
        if (running is not null) return;

        RunningJob? job = null;
        if (activeSlot is { } active)
        {
            job = new RunningJob(active, PrepPriority.Active);
            activeSlot = null;
        }
        else if (prefetchSlot is { } prefetch)
        {
            job = new RunningJob(prefetch, PrepPriority.Prefetch);
            prefetchSlot = null;
        }

        if (job is null) return;
        running = job;
        runningTask = RunJob(job);
    }

    private async Task RunJob(RunningJob job)
    {
        PrepResult result;
        try
        {
            var outPath = cacheManager.CachePathFor(job.FilePath);
            var processed =
                await prepareService.PrepareFileAsync(job.FilePath, outPath, job.Cts.Token);
            result = new PrepResult.Successful(
                processed.SyncPath, processed.Blake3Hash, processed.Sha1Hash, processed.GainDb);
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
        {
            result = new PrepResult.Preempted();
        }
        catch (Exception ex)
        {
            result = new PrepResult.Failed(ex);
        }

        mailbox.TryPost(new JobCompleted(job, result));
    }

    private void HandleCompleted(RunningJob job, PrepResult result)
    {
        if (!ReferenceEquals(running, job)) return;

        switch (result)
        {
            case PrepResult.Successful ok:
                if (job.Priority == PrepPriority.Active) lastActiveArtifact = ok.PreparedFilePath;
                cacheManager.TryEvictLru(ok.PreparedFilePath, lastActiveArtifact);
                observedResults[job.FilePath] = result;
                break;
            case PrepResult.Failed { Ex: not ConnectionLostException } failed:
                observedResults[job.FilePath] = result;
                Plugin.Log.Error(failed.Ex, "Failed to prepare file {path}", job.FilePath);
                break;
        }

        FinishJob(job, result);
        StartNextIfIdle();
    }

    private void FinishJob(RunningJob job, PrepResult result)
    {
        job.Cts.Dispose();
        running = null;
        runningTask = null;
        foreach (var waiter in job.Waiters) waiter.TrySetResult(result);
    }

    private void OnMessageError(Exception e, Message message)
    {
        Plugin.Log.Error(e, "sync-prep message failed: {message}", message);
        var failed = new PrepResult.Failed(e);
        switch (message)
        {
            case PrepRequested(_, _, var completion):
                completion.TrySetResult(failed);
                break;
            case JobCompleted(var job, _) when ReferenceEquals(running, job):
                FinishJob(job, failed);
                StartNextIfIdle();
                break;
        }
    }

    private async ValueTask OnCompleted()
    {
        Preempt(ref activeSlot);
        Preempt(ref prefetchSlot);

        if (running is not { } job) return;
        job.Cts.Cancel();
        if (runningTask is not null) await runningTask;
        if (ReferenceEquals(running, job)) FinishJob(job, new PrepResult.Preempted());
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return mailbox.DisposeAsync();
    }
}
