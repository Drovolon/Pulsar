using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Concurrency;

namespace Pulsar.Broadcast.Prepare;

internal sealed class PrefetchTiming
{
    internal int LeadMs { get; set; } = 20_000;
    internal int FinalMs { get; set; } = 5_000;
}

/// <summary>
/// Owns best-effort next-track preparation. Source observations arm the lead/final
/// checkpoints; checkpoints sample the live source because NextFilePath may change
/// without a source event.
/// </summary>
internal sealed class PrefetchScheduler : IAsyncDisposable
{
    private abstract record Message;
    private sealed record SourceObserved(IMusicSource? Source, SourceSnapshot? Snapshot) : Message;
    private sealed record Tick(ScheduledTick Schedule) : Message;
    private sealed record PreparationCompleted(
        IMusicSource Source,
        string CurrentPath,
        string NextPath,
        PrepResult Result) : Message;
    private sealed record Cleared(TaskCompletionSource Completion) : Message;

    private sealed class ScheduledTick
    {
        internal CancellationTokenSource Cts { get; } = new();
        internal Task Task { get; set; } = Task.CompletedTask;
    }

    private readonly SyncPrep prep;
    private readonly PrefetchTiming timing;
    private readonly Action<IMusicSource, string, string, PrepResult.Successful> onPrepared;
    private readonly SerializedMailbox<Message> mailbox;

    private IMusicSource? source;
    private string? currentPath;
    private bool finalPending;
    private ScheduledTick? scheduledTick;
    private readonly List<Task> retiredDelayTasks = [];

    internal PrefetchScheduler(
        SyncPrep prep,
        PrefetchTiming timing,
        Action<IMusicSource, string, string, PrepResult.Successful> onPrepared)
    {
        this.prep = prep;
        this.timing = timing;
        this.onPrepared = onPrepared;
        mailbox = new SerializedMailbox<Message>(HandleMessage, OnMessageError, OnCompleted);
    }

    internal void Observe(IMusicSource? activeSource, SourceSnapshot? snapshot)
        => mailbox.TryPost(new SourceObserved(activeSource, snapshot));

    /// <summary>
    /// Detaches from the live source before its owner disposes it. Once this completes,
    /// no checkpoint can sample that source again.
    /// </summary>
    internal Task ClearAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!mailbox.TryPost(new Cleared(completion))) completion.TrySetResult();
        return completion.Task;
    }

    private ValueTask HandleMessage(Message message)
    {
        switch (message)
        {
            case SourceObserved(var activeSource, var snapshot):
                ObserveSource(activeSource, snapshot);
                break;
            case Tick(var schedule):
                if (ReferenceEquals(scheduledTick, schedule))
                {
                    scheduledTick = null;
                    schedule.Cts.Dispose();
                    HandleTick();
                }
                break;
            case PreparationCompleted(
                var prepSource,
                var prepCurrent,
                var prepNext,
                var result):
                HandlePreparationCompleted(
                    prepSource, prepCurrent, prepNext, result);
                break;
            case Cleared(var completion):
                Clear();
                completion.TrySetResult();
                break;
        }

        return ValueTask.CompletedTask;
    }

    private static void OnMessageError(Exception e, Message message)
    {
        Plugin.Log.Verbose($"prefetch scheduling failed: {e.Message}");
        if (message is Cleared(var completion)) completion.TrySetResult();
    }

    private async ValueTask OnCompleted()
    {
        Clear();
        if (retiredDelayTasks.Count > 0) await Task.WhenAll(retiredDelayTasks);
    }

    private void ObserveSource(IMusicSource? activeSource, SourceSnapshot? snapshot)
    {
        CancelDelay();
        source = activeSource;
        currentPath = snapshot?.FilePath;
        finalPending = false;

        if (snapshot is null || !snapshot.IsPlaying)
        {
            Plugin.Log.Debug("prefetch timer: disarmed (stopped/paused)");
            return;
        }

        var durationMs = snapshot.Meta.DurationMs;
        if (durationMs <= 0)
        {
            Plugin.Log.Debug("prefetch timer: disarmed (unknown duration)");
            return;
        }

        var remainingMs = durationMs - (long)snapshot.Position.TotalMilliseconds;
        var untilLead = remainingMs - timing.LeadMs;
        if (untilLead > 0)
        {
            Plugin.Log.Debug($"prefetch timer: lead tick in {untilLead / 1000}s "
                             + $"(remaining {remainingMs / 1000}s of {durationMs / 1000}s)");
            Schedule(untilLead);
        }
        else
        {
            finalPending = true;
            Plugin.Log.Debug($"prefetch timer: final tick in "
                             + $"{Math.Max(0, remainingMs - timing.FinalMs) / 1000}s "
                             + $"(remaining {remainingMs / 1000}s, inside {timing.LeadMs / 1000}s lead)");
            ScheduleFinal(remainingMs);
        }
    }

    private void HandleTick()
    {
        var snapshot = source?.Current;
        var next = snapshot?.NextFilePath;
        Plugin.Log.Debug($"prefetch tick fired: next={(next is null ? "(null)" : Path.GetFileName(next))}");
        if (source is { } activeSource
            && snapshot is { IsPlaying: true }
            && next is not null)
        {
            Plugin.Log.Debug($"start-prefetch next: {Path.GetFileName(next)}");
            _ = ReportPreparation(
                prep.PreparePrefetch(next), activeSource, snapshot.FilePath, next);
        }

        if (!finalPending && snapshot is { IsPlaying: true, Meta.DurationMs: > 0 } live)
        {
            finalPending = true;
            var remainingMs = live.Meta.DurationMs - (long)live.Position.TotalMilliseconds;
            ScheduleFinal(remainingMs);
        }
    }

    private async Task ReportPreparation(
        Task<PrepResult> task,
        IMusicSource prepSource,
        string prepCurrent,
        string prepNext)
    {
        PrepResult result;
        try
        {
            result = await task;
        }
        catch (Exception e)
        {
            result = new PrepResult.Failed(e);
        }

        mailbox.TryPost(new PreparationCompleted(
            prepSource, prepCurrent, prepNext, result));
    }

    private void HandlePreparationCompleted(
        IMusicSource prepSource,
        string prepCurrent,
        string prepNext,
        PrepResult result)
    {
        var live = prepSource.Current;
        if (!ReferenceEquals(source, prepSource)
            || currentPath != prepCurrent
            || live?.FilePath != prepCurrent
            || live.NextFilePath != prepNext
            || result is not PrepResult.Successful success
            || !File.Exists(success.PreparedFilePath))
            return;

        onPrepared(prepSource, prepCurrent, prepNext, success);
    }

    private void ScheduleFinal(long remainingMs)
        => Schedule(Math.Max(0, remainingMs - timing.FinalMs));

    private void Schedule(long delayMs)
    {
        CancelDelay();
        var schedule = new ScheduledTick();
        scheduledTick = schedule;
        schedule.Task = PostTickAfterDelay(
            TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 0, int.MaxValue)),
            schedule);
    }

    private async Task PostTickAfterDelay(TimeSpan delay, ScheduledTick schedule)
    {
        var token = schedule.Cts.Token;
        try
        {
            await Task.Delay(delay, token);
            mailbox.TryPost(new Tick(schedule));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Clear()
    {
        CancelDelay();
        source = null;
        currentPath = null;
        finalPending = false;
    }

    private void CancelDelay()
    {
        if (scheduledTick is { } schedule)
        {
            scheduledTick = null;
            schedule.Cts.Cancel();
            if (!schedule.Task.IsCompleted) retiredDelayTasks.Add(schedule.Task);
            retiredDelayTasks.RemoveAll(static task => task.IsCompleted);
            schedule.Cts.Dispose();
        }
    }

    public ValueTask DisposeAsync() => mailbox.DisposeAsync();
}
