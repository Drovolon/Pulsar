using System;
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
/// Schedules next-track preparation from source frames without retaining source objects.
/// </summary>
internal sealed class PrefetchScheduler : IAsyncDisposable
{
    private abstract record Message;

    private sealed record SourceObserved(SourceFrame? Frame) : Message;

    private sealed record Tick(ScheduledTick Schedule) : Message;

    private sealed record PreparationCompleted(SourceCursor Cursor, string CurrentPath, string NextPath, PrepResult Result)
        : Message;

    private sealed record Cleared(TaskCompletionSource Completion) : Message;

    private sealed class ScheduledTick
    {
        internal CancellationTokenSource Cts { get; } = new();
    }

    private readonly SyncPrep prep;
    private readonly PrefetchTiming timing;
    private readonly Action<SourceCursor, string, string, PrepResult.Successful> onPrepared;
    private readonly SerializedMailbox<Message> mailbox;

    private SourceFrame? frame;
    private bool finalPending;
    private ScheduledTick? scheduledTick;

    internal PrefetchScheduler(
        SyncPrep prep, PrefetchTiming timing, Action<SourceCursor, string, string, PrepResult.Successful> onPrepared)
    {
        this.prep = prep;
        this.timing = timing;
        this.onPrepared = onPrepared;
        mailbox = new SerializedMailbox<Message>(HandleMessage, OnMessageError, OnCompleted);
    }

    internal void Observe(SourceFrame? value) => mailbox.TryPost(new SourceObserved(value));

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
            case SourceObserved(var value):
                ObserveSource(value);
                break;
            case Tick(var schedule):
                if (ReferenceEquals(scheduledTick, schedule))
                {
                    scheduledTick = null;
                    schedule.Cts.Dispose();
                    HandleTick();
                }

                break;
            case PreparationCompleted(var prepCursor, var prepCurrent, var prepNext, var result):
                HandlePreparationCompleted(prepCursor, prepCurrent, prepNext, result);
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

    private ValueTask OnCompleted()
    {
        Clear();
        return ValueTask.CompletedTask;
    }

    private void ObserveSource(SourceFrame? value)
    {
        if (value is not null && ReferenceEquals(frame?.Cursor, value.Cursor))
        {
            var nextChanged = frame.Snapshot.NextFilePath != value.Snapshot.NextFilePath;
            frame = value;
            if (nextChanged && scheduledTick is null && value.Snapshot.IsPlaying)
                HandleTick();
            return;
        }

        CancelDelay();
        frame = value;
        finalPending = false;

        var snapshot = value?.Snapshot;

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

        var remainingMs = RemainingMs(snapshot);
        var untilLead = remainingMs - timing.LeadMs;
        if (untilLead > 0)
        {
            Plugin.Log.Debug($"prefetch timer: lead tick in {untilLead / 1000}s " +
                             $"(remaining {remainingMs / 1000}s of {durationMs / 1000}s)");
            Schedule(untilLead);
        }
        else
        {
            finalPending = true;
            Plugin.Log.Debug($"prefetch timer: final tick in " +
                             $"{Math.Max(0, remainingMs - timing.FinalMs) / 1000}s " +
                             $"(remaining {remainingMs / 1000}s, inside {timing.LeadMs / 1000}s lead)");
            ScheduleFinal(remainingMs);
        }
    }

    private void HandleTick()
    {
        var current = frame;
        var snapshot = current?.Snapshot;
        var next = snapshot?.NextFilePath;
        Plugin.Log.Debug($"prefetch tick fired: next={(next is null ? "(null)" : Path.GetFileName(next))}");
        if (current is { } active && snapshot is { IsPlaying: true } && next is not null)
        {
            Plugin.Log.Debug($"start-prefetch next: {Path.GetFileName(next)}");
            _ = ReportPreparation(prep.PreparePrefetch(next), active.Cursor, snapshot.FilePath, next);
        }

        if (!finalPending && snapshot is { IsPlaying: true, Meta.DurationMs: > 0 } live)
        {
            finalPending = true;
            var remainingMs = RemainingMs(live);
            ScheduleFinal(remainingMs);
        }
    }

    private static long RemainingMs(SourceSnapshot snapshot)
    {
        var elapsed = snapshot.IsPlaying ? DateTimeOffset.UtcNow - snapshot.AsOf : TimeSpan.Zero;
        return snapshot.Meta.DurationMs -
               (long)snapshot.Position.TotalMilliseconds -
               (long)Math.Max(0, elapsed.TotalMilliseconds);
    }

    private async Task ReportPreparation(Task<PrepResult> task, SourceCursor prepCursor, string prepCurrent, string prepNext)
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

        mailbox.TryPost(new PreparationCompleted(prepCursor, prepCurrent, prepNext, result));
    }

    private void HandlePreparationCompleted(SourceCursor prepCursor, string prepCurrent, string prepNext, PrepResult result)
    {
        var live = frame;
        if (!ReferenceEquals(live?.Cursor, prepCursor) ||
            live.Snapshot.FilePath != prepCurrent ||
            live.Snapshot.NextFilePath != prepNext ||
            result is not PrepResult.Successful success ||
            !File.Exists(success.PreparedFilePath))
            return;

        onPrepared(prepCursor, prepCurrent, prepNext, success);
    }

    private void ScheduleFinal(long remainingMs) => Schedule(Math.Max(0, remainingMs - timing.FinalMs));

    private void Schedule(long delayMs)
    {
        CancelDelay();
        var schedule = new ScheduledTick();
        scheduledTick = schedule;
        _ = PostTickAfterDelay(TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 0, int.MaxValue)), schedule);
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
        frame = null;
        finalPending = false;
    }

    private void CancelDelay()
    {
        if (scheduledTick is { } schedule)
        {
            scheduledTick = null;
            schedule.Cts.Cancel();
            schedule.Cts.Dispose();
        }
    }

    public ValueTask DisposeAsync() => mailbox.DisposeAsync();
}
