using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Concurrency;

namespace Pulsar.Broadcast.Prepare;

/// <summary>
/// Owns best-effort next-track preparation. Source observations arm the lead/final
/// checkpoints; checkpoints sample the live source because NextFilePath may change
/// without a source event.
/// </summary>
internal sealed class PrefetchScheduler : IAsyncDisposable
{
    private abstract record Message;
    private sealed record SourceObserved(IMusicSource? Source, SourceSnapshot? Snapshot) : Message;
    private sealed record Tick(long Generation) : Message;
    private sealed record Cleared(TaskCompletionSource Completion) : Message;

    private readonly SyncPrep prep;
    private readonly Configuration config;
    private readonly SerializedMailbox<Message> mailbox;

    private IMusicSource? source;
    private long generation;
    private bool finalPending;
    private CancellationTokenSource? delayCts;
    private Task? delayTask;
    private readonly List<Task> retiredDelayTasks = [];

    internal PrefetchScheduler(SyncPrep prep, Configuration config)
    {
        this.prep = prep;
        this.config = config;
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
            case Tick(var tickGeneration):
                if (tickGeneration == generation) HandleTick();
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
        generation++;
        CancelDelay();
        source = activeSource;
        finalPending = false;

        if (snapshot?.NextFilePath is { } next)
        {
            Plugin.Log.Debug($"start-prefetch next: {Path.GetFileName(next)}");
            _ = prep.PreparePrefetch(next);
        }
        else Plugin.Log.Debug("start-prefetch: no track available to prefetch");

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
        var untilLead = remainingMs - config.PrefetchLeadMs;
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
                             + $"{Math.Max(0, remainingMs - config.PrefetchFinalMs) / 1000}s "
                             + $"(remaining {remainingMs / 1000}s, inside {config.PrefetchLeadMs / 1000}s lead)");
            ScheduleFinal(remainingMs, config);
        }
    }

    private void HandleTick()
    {
        var snapshot = source?.Current;
        var next = snapshot?.NextFilePath;
        Plugin.Log.Debug($"prefetch tick fired: next={(next is null ? "(null)" : Path.GetFileName(next))}");
        if (next is not null) _ = prep.PreparePrefetch(next);

        if (!finalPending && snapshot is { Meta.DurationMs: > 0 } live)
        {
            finalPending = true;
            var remainingMs = live.Meta.DurationMs - (long)live.Position.TotalMilliseconds;
            ScheduleFinal(remainingMs, config);
        }
    }

    private void ScheduleFinal(long remainingMs, Configuration cfg)
        => Schedule(Math.Max(0, remainingMs - cfg.PrefetchFinalMs));

    private void Schedule(long delayMs)
    {
        CancelDelay();
        delayCts = new CancellationTokenSource();
        delayTask = PostTickAfterDelay(
            TimeSpan.FromMilliseconds(Math.Clamp(delayMs, 0, int.MaxValue)),
            generation,
            delayCts.Token);
    }

    private async Task PostTickAfterDelay(TimeSpan delay, long tickGeneration, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
            mailbox.TryPost(new Tick(tickGeneration));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void Clear()
    {
        generation++;
        CancelDelay();
        source = null;
        finalPending = false;
    }

    private void CancelDelay()
    {
        if (delayTask is not null)
        {
            delayCts?.Cancel();
            if (!delayTask.IsCompleted) retiredDelayTasks.Add(delayTask);
            delayTask = null;
            retiredDelayTasks.RemoveAll(static task => task.IsCompleted);
        }
        delayCts?.Dispose();
        delayCts = null;
    }

    public ValueTask DisposeAsync() => mailbox.DisposeAsync();
}
