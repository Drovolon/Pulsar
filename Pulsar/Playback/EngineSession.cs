using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Common.Api;
using Pulsar.Concurrency;
using StreamJsonRpc;

namespace Pulsar.Playback;

internal sealed record EngineTarget(long Revision, string Path, PlaybackState State, TimeSpan Position);

internal sealed record EngineObservation(long Revision, EngineSnapshot Snapshot, bool Discrete);

internal sealed record EngineSessionEnded(long Revision, EndReason Reason);

/// <summary>
/// Manages an IRemoteEngine "session". Core concept: there's an engine "target".
/// This class figures out how to make the engine match the target. That includes
/// serializing commands, having command generations to avoid duplicates
/// or out-of-order callbacks, reconnecting and resetting volume after reconnect, etc.
/// </summary>
internal sealed class EngineSession : IAsyncDisposable
{
    private abstract record Message(TaskCompletionSource? Completion = null);

    private sealed record TargetSet(EngineTarget? Target) : Message;

    private sealed record StopRequested(TaskCompletionSource Done) : Message(Done);

    private sealed record VolumeSet(float Value) : Message;

    private sealed record SeekRequested(TimeSpan Position) : Message;

    private sealed record SnapshotRequested(TaskCompletionSource<EngineSnapshot> Done) : Message;

    private sealed record Reconnected : Message;

    private sealed record Updated(EngineSnapshot Value) : Message;

    private sealed record PollTick(long Generation) : Message;

    private sealed record PollCompleted(long Generation, EngineSnapshot? Snapshot, Exception? Error) : Message;

    private readonly IRemoteEngine engine;
    private readonly SerializedMailbox<Message> mailbox;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly Lazy<Task> disposeTask;
    private readonly TimeSpan pollPlaying;
    private readonly TimeSpan pollIdle;
    private readonly TimeSpan errorBackoff;
    private readonly TimeSpan lostBackoff;
    private readonly TimeSpan disposeTimeout;

    private EngineTarget? desired;
    private EngineTarget? commanded;
    private volatile EngineSnapshot confirmed = new(PlaybackState.Stopped, null, null, null, DateTimeOffset.UtcNow);
    private bool available = true;
    private float latestVolume = -1f;
    private long nextPlaybackId;
    private long activePlaybackId;
    private long activeRevision;
    private long activeSequence;

    private long pollGeneration;
    private CancellationTokenSource? pollDelayCts;
    private Task? pollDelayTask;
    private Task? pollTask;

    internal EngineSession(IRemoteEngine engine) : this(engine, TimeSpan.FromMilliseconds(250),
                                                        TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1),
                                                        TimeSpan.FromSeconds(2)) { }

    internal EngineSession(
        IRemoteEngine engine, TimeSpan pollPlaying, TimeSpan pollIdle, TimeSpan errorBackoff, TimeSpan disposeTimeout,
        TimeSpan? lostBackoff = null)
    {
        this.engine = engine;
        this.pollPlaying = pollPlaying;
        this.pollIdle = pollIdle;
        this.errorBackoff = errorBackoff;
        this.lostBackoff = lostBackoff ?? errorBackoff;
        this.disposeTimeout = disposeTimeout;
        mailbox = new SerializedMailbox<Message>(HandleMessage, OnMessageError, OnCompleted);
        disposeTask = new Lazy<Task>(FinishDispose, LazyThreadSafetyMode.ExecutionAndPublication);
        engine.OnUpdated += OnEngineUpdated;
        SchedulePoll(pollIdle);
    }

    internal event Action<EngineObservation>? OnObserved;
    internal event Action<EngineSessionEnded>? OnPlaybackEnded;
    internal event Action? OnReconnected;
    internal EngineSnapshot Snapshot => confirmed;

    internal void SetTarget(EngineTarget? target) => mailbox.TryPost(new TargetSet(target));
    internal void SetVolume(float value) => mailbox.TryPost(new VolumeSet(value));
    internal void Seek(TimeSpan position) => mailbox.TryPost(new SeekRequested(position));
    internal void OnEngineReconnected() => mailbox.TryPost(new Reconnected());

    internal async Task<EngineSnapshot> RefreshAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<EngineSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cancellation = token.Register(() => completion.TrySetCanceled(token));
        if (!mailbox.TryPost(new SnapshotRequested(completion)))
            throw new ObjectDisposedException(nameof(EngineSession));
        return await completion.Task;
    }

    internal Task StopAsync()
    {
        var completion = NewCompletion();
        if (!mailbox.TryPost(new StopRequested(completion)))
            completion.TrySetException(new ObjectDisposedException(nameof(EngineSession)));
        return completion.Task;
    }

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async ValueTask HandleMessage(Message message)
    {
        switch (message)
        {
            case TargetSet(var target):
                desired = target;
                await Reconcile();
                break;
            case StopRequested:
                desired = null;
                await Reconcile();
                break;
            case VolumeSet(var value):
                latestVolume = value;
                await engine.SetVolumeAsync(value, lifetimeCts.Token);
                break;
            case SeekRequested(var position):
                await engine.SeekAsync(position, lifetimeCts.Token);
                break;
            case SnapshotRequested(var done):
                if (done.Task.IsCompleted) break;
                try
                {
                    var snapshot = await engine.GetStateAsync(lifetimeCts.Token);
                    ApplyUpdate(snapshot, false);
                    done.TrySetResult(confirmed);
                }
                catch (Exception e)
                {
                    done.TrySetException(e);
                }

                break;
            case Reconnected:
                available = true;
                commanded = null;
                activePlaybackId = 0;
                activeRevision = 0;
                activeSequence = 0;
                confirmed = new EngineSnapshot(PlaybackState.Stopped, null, null, null, DateTimeOffset.UtcNow);
                ReschedulePoll(TimeSpan.Zero);
                OnReconnected?.Invoke();
                if (latestVolume >= 0)
                    await engine.SetVolumeAsync(latestVolume, lifetimeCts.Token);
                break;
            case Updated(var update):
                ApplyUpdate(update, true);
                break;
            case PollTick(var generation):
                HandlePollTick(generation);
                break;
            case PollCompleted(var generation, var snapshot, var error):
                HandlePollCompleted(generation, snapshot, error);
                break;
        }

        message.Completion?.TrySetResult();
    }

    private async Task Reconcile()
    {
        if (!available) return;

        if (desired is null)
        {
            commanded = null;
            if (activePlaybackId != 0 || confirmed.Path is not null)
                await engine.StopAsync(lifetimeCts.Token);
            return;
        }

        if (commanded is null ||
            commanded.Revision != desired.Revision ||
            !string.Equals(commanded.Path, desired.Path, StringComparison.OrdinalIgnoreCase))
        {
            var target = desired;
            var playbackId = Interlocked.Increment(ref nextPlaybackId);
            activePlaybackId = playbackId;
            activeRevision = target.Revision;
            activeSequence = 0;
            try
            {
                await engine.LoadFileAsync(target.Path, target.Position, target.State == PlaybackState.Playing, playbackId,
                                           lifetimeCts.Token);
                commanded = target;
            }
            catch (Exception e)
            {
                HandleDispatchFailure(target, playbackId, e);
            }

            return;
        }

        if (commanded.State == desired.State) return;
        var requested = desired;
        try
        {
            if (requested.State == PlaybackState.Playing)
                await engine.ResumeAsync(lifetimeCts.Token);
            else
                await engine.PauseAsync(lifetimeCts.Token);
            commanded = requested;
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Engine transport command failed");
        }
    }

    private void HandleDispatchFailure(EngineTarget target, long playbackId, Exception error)
    {
        if (activePlaybackId != playbackId) return;
        var reason = error is ConnectionLostException ? EndReason.Disconnected : EndReason.Failed;
        activePlaybackId = 0;
        activeRevision = 0;
        activeSequence = 0;
        commanded = null;
        confirmed = new EngineSnapshot(PlaybackState.Stopped, null, error.Message, null, DateTimeOffset.UtcNow, playbackId,
                                       TerminalReason: reason);
        if (reason == EndReason.Disconnected) available = false;
        Plugin.Log.Error(error, "Engine load dispatch failed for {path}", target.Path);
        OnPlaybackEnded?.Invoke(new EngineSessionEnded(target.Revision, reason));
    }

    private void ApplyUpdate(EngineSnapshot update, bool discrete)
    {
        if (update.PlaybackId != activePlaybackId) return;
        if (update.TerminalReason is not null && update.State != PlaybackState.Stopped)
        {
            Plugin.Log.Warning("Engine emitted a non-stopped terminal update for playback {playbackId}", update.PlaybackId);
            return;
        }

        if (update.Sequence <= activeSequence) return;
        if (update.State != PlaybackState.Stopped &&
            commanded is not null &&
            !string.Equals(update.Path, commanded.Path, StringComparison.OrdinalIgnoreCase))
            return;

        var revision = activeRevision;
        activeSequence = update.Sequence;
        confirmed = update;

        if (update.TerminalReason is { } terminalReason)
        {
            activePlaybackId = 0;
            activeRevision = 0;
            activeSequence = 0;
            commanded = null;
            if (terminalReason == EndReason.Disconnected) available = false;
            OnPlaybackEnded?.Invoke(new EngineSessionEnded(revision, terminalReason));
            return;
        }

        if (commanded is not null &&
            update.State != PlaybackState.Stopped &&
            string.Equals(update.Path, commanded.Path, StringComparison.OrdinalIgnoreCase))
        {
            // Polls can correct the state after a failed device operation.
            commanded = commanded with { State = update.State };
        }

        if (update.State == PlaybackState.Stopped && desired is null)
        {
            activePlaybackId = 0;
            activeRevision = 0;
            activeSequence = 0;
            commanded = null;
        }

        OnObserved?.Invoke(new EngineObservation(revision, update, discrete));
    }

    private void HandlePollTick(long generation)
    {
        if (generation != pollGeneration) return;
        pollDelayCts?.Dispose();
        pollDelayCts = null;
        pollDelayTask = null;

        var confirmationPending = desired is not null &&
                                  (confirmed.State != desired.State ||
                                   !string.Equals(confirmed.Path, desired.Path, StringComparison.OrdinalIgnoreCase));
        if (confirmed.State != PlaybackState.Playing && !confirmationPending)
        {
            SchedulePoll(pollIdle);
            return;
        }

        pollTask = PollEngine(generation, lifetimeCts.Token);
    }

    private async Task PollEngine(long generation, CancellationToken token)
    {
        EngineSnapshot? snapshot = null;
        Exception? error = null;
        try
        {
            snapshot = await engine.GetStateAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            error = e;
        }

        mailbox.TryPost(new PollCompleted(generation, snapshot, error));
    }

    private void HandlePollCompleted(long generation, EngineSnapshot? snapshot, Exception? error)
    {
        if (generation != pollGeneration) return;
        pollTask = null;
        if (error is not null)
        {
            Plugin.Log.Error(error, "Engine position poll failed");
            SchedulePoll(error is ConnectionLostException ? lostBackoff : errorBackoff);
            return;
        }

        if (snapshot is not null) ApplyUpdate(snapshot, false);
        SchedulePoll(confirmed.State == PlaybackState.Playing ? pollPlaying : pollIdle);
    }

    private void SchedulePoll(TimeSpan delay)
    {
        var generation = ++pollGeneration;
        pollDelayCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCts.Token);
        pollDelayTask = PostPollAfterDelay(delay, generation, pollDelayCts.Token);
    }

    private void ReschedulePoll(TimeSpan delay)
    {
        if (pollTask is not null) return;
        pollDelayCts?.Cancel();
        pollDelayCts?.Dispose();
        pollDelayCts = null;
        pollDelayTask = null;
        SchedulePoll(delay);
    }

    private async Task PostPollAfterDelay(TimeSpan delay, long generation, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
            mailbox.TryPost(new PollTick(generation));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void OnEngineUpdated(object? _, EngineSnapshot update) => mailbox.TryPost(new Updated(update));

    private void OnMessageError(Exception error, Message message)
    {
        message.Completion?.TrySetException(error);
        if (error is OperationCanceledException && lifetimeCts.IsCancellationRequested) return;
        Plugin.Log.Error(error, "EngineSession message failed: {message}", message);
    }

    private async ValueTask OnCompleted()
    {
        pollDelayCts?.Cancel();
        if (pollDelayTask is not null) await pollDelayTask;
        if (pollTask is not null)
        {
            try
            {
                await pollTask.WaitAsync(disposeTimeout);
            }
            catch (TimeoutException)
            {
                Plugin.Log.Debug("EngineSession: position poll did not stop during disposal");
            }
        }

        pollDelayCts?.Dispose();
    }

    private async Task FinishDispose()
    {
        engine.OnUpdated -= OnEngineUpdated;
        lifetimeCts.Cancel();
        var drain = mailbox.DisposeAsync().AsTask();
        try
        {
            await drain.WaitAsync(disposeTimeout);
            lifetimeCts.Dispose();
        }
        catch (TimeoutException)
        {
            Plugin.Log.Debug("EngineSession command queue did not drain during disposal");
            _ = DisposeLifetimeWhenDrained(drain);
        }
    }

    private async Task DisposeLifetimeWhenDrained(Task drain)
    {
        try
        {
            await drain;
        }
        catch
        {
            /* command failures were already logged */
        }

        lifetimeCts.Dispose();
    }

    public ValueTask DisposeAsync() => new(disposeTask.Value);
}
