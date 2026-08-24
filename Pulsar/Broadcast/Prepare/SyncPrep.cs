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

internal abstract record PrepResult
{
    public sealed record Successful(
        string PreparedFilePath,
        string Blake3Hash,
        string Sha1Hash,
        double GainDb,
        PrepInput Input) : PrepResult;

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
internal sealed class SyncPrep : IAsyncDisposable
{
    private enum PrepPriority
    {
        Prefetch,
        Active,
    }

    private abstract record Message;

    private sealed record PrepRequested(PrepInput Input, PrepPriority Priority, TaskCompletionSource<PrepResult> Completion)
        : Message;

    private sealed record JobCompleted(RunningJob Job, PrepResult Result) : Message;

    private sealed record Pending(PrepInput Input, TaskCompletionSource<PrepResult> Tcs);

    private sealed class RunningJob(Pending pending, PrepPriority priority)
    {
        internal PrepInput Input { get; } = pending.Input;
        internal string FilePath => Input.FilePath;
        internal PrepPriority Priority { get; set; } = priority;
        internal CancellationTokenSource Cts { get; } = new();
        internal List<TaskCompletionSource<PrepResult>> Waiters { get; } = [pending.Tcs];
    }

    private readonly SerializedMailbox<Message> mailbox;

    // Currently running transcode. New prep request for the same path gets given that
    // task, instead of canceling and spawning a new one.
    private RunningJob? running;
    private Task? runningTask;

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
    private readonly ConcurrentDictionary<PrepInput, PrepResult> observedResults = new();

    private readonly Dictionary<string, int> artifactPins = [];
    private readonly object artifactGate = new();

    private readonly CacheManager cacheManager;
    private readonly IPrepareService prepareService;
    private readonly TimeSpan attemptTimeout;

    public SyncPrep(CacheManager cacheManager, IPrepareService prepareService, TimeSpan? attemptTimeout = null)
    {
        this.cacheManager = cacheManager;
        this.prepareService = prepareService;
        this.attemptTimeout = attemptTimeout ?? TimeSpan.FromMinutes(2);
        mailbox = new SerializedMailbox<Message>(HandleMessage, OnMessageError, OnCompleted);
    }

    public Task<PrepResult> PrepareActive(string filePath) => Prep(filePath, PrepPriority.Active);

    public Task<PrepResult> PreparePrefetch(string filePath) => Prep(filePath, PrepPriority.Prefetch);

    internal Task<PrepResult> PrepareActive(PrepInput input) => Prep(input, PrepPriority.Active);
    internal Task<PrepResult> PreparePrefetch(PrepInput input) => Prep(input, PrepPriority.Prefetch);

    public bool TryGet(string key, out PrepResult? result)
    {
        try
        {
            return observedResults.TryGetValue(PrepInput.Capture(key), out result);
        }
        catch (IOException)
        {
            result = null;
            return false;
        }
    }

    internal bool TryGet(PrepInput input, out PrepResult? result) => observedResults.TryGetValue(input, out result);

    internal ArtifactLease PinArtifact(string path)
    {
        lock (artifactGate)
        {
            artifactPins[path] = artifactPins.GetValueOrDefault(path) + 1;
        }

        return new ArtifactLease(this, path);
    }

    internal bool TryPinArtifact(string path, out ArtifactLease lease)
    {
        lock (artifactGate)
        {
            if (!File.Exists(path))
            {
                lease = null!;
                return false;
            }

            artifactPins[path] = artifactPins.GetValueOrDefault(path) + 1;
            lease = new ArtifactLease(this, path);
            return true;
        }
    }

    internal bool TryAcquire(PrepInput input, out PrepResult.Successful success, out ArtifactLease lease)
    {
        lock (artifactGate)
        {
            if (observedResults.TryGetValue(input, out var known) &&
                known is PrepResult.Successful prepared &&
                File.Exists(prepared.PreparedFilePath))
            {
                artifactPins[prepared.PreparedFilePath] = artifactPins.GetValueOrDefault(prepared.PreparedFilePath) + 1;
                success = prepared;
                lease = new ArtifactLease(this, prepared.PreparedFilePath);
                return true;
            }
        }

        success = null!;
        lease = null!;
        return false;
    }

    private Task<PrepResult> Prep(string filePath, PrepPriority priority)
    {
        try
        {
            return Prep(PrepInput.Capture(filePath), priority);
        }
        catch (Exception e)
        {
            return Task.FromResult<PrepResult>(new PrepResult.Failed(e));
        }
    }

    private Task<PrepResult> Prep(PrepInput input, PrepPriority priority)
    {
        var completion = NewCompletion();
        if (!mailbox.TryPost(new PrepRequested(input, priority, completion)))
            completion.TrySetResult(new PrepResult.Preempted());
        return completion.Task;
    }

    private static TaskCompletionSource<PrepResult> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool TryGetPrepared(PrepInput input, out PrepResult.Successful success)
    {
        if (observedResults.TryGetValue(input, out var known) &&
            known is PrepResult.Successful prepared &&
            File.Exists(prepared.PreparedFilePath))
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
            case PrepRequested(var input, var priority, var completion):
                HandleRequest(input, priority, completion);
                break;
            case JobCompleted(var job, var result):
                HandleCompleted(job, result);
                break;
        }

        return ValueTask.CompletedTask;
    }

    private void HandleRequest(PrepInput input, PrepPriority priority, TaskCompletionSource<PrepResult> completion)
    {
        if (TryGetPrepared(input, out var success))
        {
            ApplySupersession(input, priority);
            CacheManager.Touch(success.PreparedFilePath);
            completion.TrySetResult(success);
            StartNextIfIdle();
            return;
        }

        if (running is { } current && current.Input == input && !current.Cts.IsCancellationRequested)
        {
            current.Waiters.Add(completion);
            if (priority == PrepPriority.Active) current.Priority = PrepPriority.Active;
            return;
        }

        ApplySupersession(input, priority);
        Queue(new Pending(input, completion), priority);
        StartNextIfIdle();
    }

    // The latest request of a priority supersedes stale work of that priority;
    // active work may also preempt a different running prefetch.
    private void ApplySupersession(PrepInput input, PrepPriority priority)
    {
        if (priority == PrepPriority.Active)
        {
            Preempt(ref activeSlot);
            if (prefetchSlot?.Input == input) Preempt(ref prefetchSlot);
            if (running is { } job && job.Input != input) job.Cts.Cancel();
        }
        else
        {
            Preempt(ref prefetchSlot);
            if (running is { Priority: PrepPriority.Prefetch } job && job.Input != input)
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
        Task<PreparedTrack>? serviceTask = null;
        var attemptPath = cacheManager.AttemptPathFor(job.Input);
        try
        {
            if (!job.Input.IsCurrent())
                throw new IOException($"Preparation input changed before processing: {job.FilePath}");
            serviceTask = prepareService.PrepareFileAsync(job.FilePath, attemptPath, job.Cts.Token);
            var processed = await serviceTask.WaitAsync(attemptTimeout, job.Cts.Token);

            if (!job.Input.IsCurrent())
                throw new IOException($"Preparation input changed while processing: {job.FilePath}");

            var preparedPath = processed.SyncPath;
            if (PathsEqual(preparedPath, attemptPath))
            {
                preparedPath = cacheManager.CachePathFor(job.Input);
                File.Move(attemptPath, preparedPath, true);
            }

            if (!job.Input.IsCurrent())
                throw new IOException($"Preparation input changed while committing: {job.FilePath}");

            result = new PrepResult.Successful(preparedPath, processed.Blake3Hash, processed.Sha1Hash, processed.GainDb,
                                               job.Input);
        }
        catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
        {
            result = new PrepResult.Preempted();
        }
        catch (TimeoutException ex)
        {
            job.Cts.Cancel();
            result = new PrepResult.Failed(ex);
        }
        catch (Exception ex)
        {
            result = new PrepResult.Failed(ex);
        } finally
        {
            if (serviceTask is { IsCompleted: false })
                _ = ObserveAbandoned(serviceTask, attemptPath);
            else
                CleanupAttempt(attemptPath);
        }

        mailbox.TryPost(new JobCompleted(job, result));
    }

    private static async Task ObserveAbandoned(Task task, string attemptPath)
    {
        try
        {
            await task;
        }
        catch
        {
            /* logical attempt already settled; observe the physical task */
        } finally
        {
            CleanupAttempt(attemptPath);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void CleanupAttempt(string attemptPath)
    {
        DeleteIfPresent(attemptPath);
        DeleteIfPresent(attemptPath + ".tmp");
    }

    private static void DeleteIfPresent(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to clean preparation attempt {path}", path);
        }
    }

    private void HandleCompleted(RunningJob job, PrepResult result)
    {
        if (!ReferenceEquals(running, job)) return;

        switch (result)
        {
            case PrepResult.Successful ok:
                lock (artifactGate)
                {
                    cacheManager.TryEvictLru([ok.PreparedFilePath, .. artifactPins.Keys]);
                    observedResults[job.Input] = result;
                }

                break;
            case PrepResult.Failed { Ex: not ConnectionLostException } failed:
                observedResults[job.Input] = result;
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

    private void ReleaseArtifact(string path)
    {
        lock (artifactGate)
        {
            if (!artifactPins.TryGetValue(path, out var count)) return;
            if (count == 1) artifactPins.Remove(path);
            else artifactPins[path] = count - 1;
        }
    }

    internal sealed class ArtifactLease : IDisposable
    {
        private SyncPrep? owner;
        private readonly string path;

        internal ArtifactLease(SyncPrep owner, string path)
        {
            this.owner = owner;
            this.path = path;
        }

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseArtifact(path);
    }
}
