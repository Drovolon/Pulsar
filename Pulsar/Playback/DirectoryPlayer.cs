using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Common.Api;
using Pulsar.Concurrency;

namespace Pulsar.Playback;

/// <summary>An immutable, atomically published view of a directory player's state.</summary>
public sealed record DirectoryPlayerView(
    IReadOnlyList<string> Tracks,
    int Index,
    bool Shuffle,
    PlaybackPosition? Position,
    PlaybackState State,
    string? CurrentTrack,
    string? NextTrack);

/// <summary>
/// Scans a directory into a lightweight playlist and coordinates ordered playback through a
/// remote audio engine. One mailbox owns playlist, playback, polling, and failure state.
/// </summary>
public sealed class DirectoryPlayer : IAsyncDisposable
{
    private abstract record Message(TaskCompletionSource? Completion = null);
    private sealed record Initialized(string[] Files, TaskCompletionSource Done) : Message(Done);
    private sealed record Rescanned(string[] Files, TaskCompletionSource Done) : Message(Done);
    private sealed record PlayIndexRequested(int Value, TaskCompletionSource Done) : Message(Done);
    private sealed record PlayRequested(TaskCompletionSource Done) : Message(Done);
    private sealed record StopRequested(TaskCompletionSource Done) : Message(Done);
    private sealed record NextRequested(TaskCompletionSource Done) : Message(Done);
    private sealed record PreviousRequested(TaskCompletionSource Done) : Message(Done);
    private sealed record ToggleShuffleRequested(TaskCompletionSource Done) : Message(Done);
    private sealed record PauseRequested : Message;
    private sealed record ResumeRequested : Message;
    private sealed record VolumeRequested(float Value) : Message;
    private sealed record SeekRequested(TimeSpan Position) : Message;
    private sealed record ReapplyVolumeRequested : Message;
    private sealed record EngineChanged(EngineSnapshot Snapshot) : Message;
    private sealed record EngineEnded(EndReason Reason) : Message;
    private sealed record PollTick(long Generation) : Message;
    private sealed record PollCompleted(
        long Generation,
        EngineSnapshot? Snapshot,
        Exception? Error) : Message;

    private static readonly string[] Extensions = [
        "*.aac", "*.aiff", "*.flac", "*.m4a",
        "*.mp3", "*.ogg", "*.opus", "*.wav",
        "*.wma", "*.wv", "*.scd"
    ];

    private readonly IRemoteEngine player;
    private readonly EngineQueueManager engineCommands;
    private readonly SerializedMailbox<Message> mailbox;
    private readonly CancellationTokenSource asyncCts = new();

    private readonly TimeSpan pollPlaying;
    private readonly TimeSpan pollIdle;
    private readonly TimeSpan errorBackoff;
    private readonly TimeSpan disposeTimeout;

    // Actor-owned state.
    private string[]? originalFiles;
    private string[]? playlist;
    private int index;
    private bool shuffle;
    private bool playing;
    private int consecutiveFailures;
    private PlaybackPosition? position;
    private PlaybackState state;

    private long pollGeneration;
    private CancellationTokenSource? pollDelayCts;
    private Task? pollDelayTask;
    private Task? pollTask;

    private volatile DirectoryPlayerView published =
        new([], 0, false, null, PlaybackState.Stopped, null, null);

    public string Directory { get; init; }
    public DirectoryPlayerView View => published;
    public IReadOnlyList<string> Tracks => published.Tracks;
    public int Index => published.Index;
    public bool Shuffle => published.Shuffle;
    public PlaybackPosition? Position => published.Position;
    public PlaybackState State => published.State;
    public string? CurrentTrack => published.CurrentTrack;
    public string? NextTrack => published.NextTrack;

    /// <summary>Fires for discrete engine cursor events, after View has been published.</summary>
    public event Action<EngineSnapshot>? OnSnapshotChanged;

    public DirectoryPlayer(IRemoteEngine player, string directory)
        : this(player, directory,
               pollPlaying: TimeSpan.FromMilliseconds(250),
               pollIdle: TimeSpan.FromMilliseconds(500),
               errorBackoff: TimeSpan.FromSeconds(1),
               disposeTimeout: TimeSpan.FromSeconds(2)) { }

    // for unit tests only
    internal DirectoryPlayer(IRemoteEngine player, string directory,
        TimeSpan pollPlaying, TimeSpan pollIdle, TimeSpan errorBackoff, TimeSpan disposeTimeout)
    {
        this.player = player;
        Directory = directory;
        this.pollPlaying = pollPlaying;
        this.pollIdle = pollIdle;
        this.errorBackoff = errorBackoff;
        this.disposeTimeout = disposeTimeout;
        engineCommands = new EngineQueueManager(player, asyncCts.Token);
        mailbox = new SerializedMailbox<Message>(HandleMessage, OnMessageError, OnCompleted);
    }

    /// <summary>Recursively scans the directory and starts observing the engine.</summary>
    public async Task Initialize()
    {
        var files = await Task.Run(ScanFiles);
        var completion = NewCompletion();
        if (!mailbox.TryPost(new Initialized(files, completion)))
            throw new ObjectDisposedException(nameof(DirectoryPlayer));
        await completion.Task;

        player.OnPlaybackEnded += OnEngineEnded;
        player.OnChanged += OnEngineChanged;
    }

    public async Task Rescan()
    {
        string[] files;
        try
        {
            files = await Task.Run(ScanFiles);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "DirectoryPlayer: rescan failed");
            return;
        }

        var completion = NewCompletion();
        if (!mailbox.TryPost(new Rescanned(files, completion))) return;
        await completion.Task;
    }

    public void PlayIndex(int value) => PostAndWait(done => new PlayIndexRequested(value, done));
    public void Play() => PostAndWait(done => new PlayRequested(done));
    public void Stop() => PostAndWait(done => new StopRequested(done));
    public void Next() => PostAndWait(done => new NextRequested(done));
    public void Prev() => PostAndWait(done => new PreviousRequested(done));
    public void ToggleShuffle() => PostAndWait(done => new ToggleShuffleRequested(done));

    public void Pause() => mailbox.TryPost(new PauseRequested());
    public void Resume() => mailbox.TryPost(new ResumeRequested());
    public void Volume(float value) => mailbox.TryPost(new VolumeRequested(value));
    public void Seek(TimeSpan value) => mailbox.TryPost(new SeekRequested(value));
    public void ReapplyVolume() => mailbox.TryPost(new ReapplyVolumeRequested());

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void PostAndWait(Func<TaskCompletionSource, Message> create)
    {
        var completion = NewCompletion();
        if (mailbox.TryPost(create(completion))) completion.Task.GetAwaiter().GetResult();
    }

    private ValueTask HandleMessage(Message message)
    {
        EngineSnapshot? notify = null;
        switch (message)
        {
            case Initialized(var files, _):
                originalFiles = files;
                playlist = files;
                SchedulePoll(pollIdle);
                break;

            case Rescanned(var files, _):
                ApplyRescan(files);
                break;

            case PlayIndexRequested(var value, _):
                if (playlist is not null && value >= 0 && value < playlist.Length)
                {
                    index = value;
                    playing = true;
                    consecutiveFailures = 0;
                    PlayCurrent();
                }
                break;

            case PlayRequested:
                playing = true;
                consecutiveFailures = 0;
                PlayCurrent();
                break;

            case StopRequested:
                playing = false;
                consecutiveFailures = 0;
                engineCommands.Stop();
                break;

            case NextRequested:
                playing = true;
                consecutiveFailures = 0;
                Advance();
                break;

            case PreviousRequested:
                playing = true;
                consecutiveFailures = 0;
                if (position is null || position.Current.TotalSeconds <= 5)
                {
                    index--;
                    if (index < 0) index++;
                }
                PlayCurrent();
                break;

            case ToggleShuffleRequested:
                ToggleShuffleCore();
                break;

            case PauseRequested:
                engineCommands.Pause();
                break;
            case ResumeRequested:
                engineCommands.Resume();
                break;
            case VolumeRequested(var value):
                engineCommands.Volume(value);
                break;
            case SeekRequested(var value):
                engineCommands.Seek(value);
                break;
            case ReapplyVolumeRequested:
                engineCommands.ReapplyVolume();
                break;

            case EngineChanged(var snapshot):
                state = snapshot.State;
                position = snapshot.Position;
                notify = snapshot;
                break;

            case EngineEnded(var reason):
                HandleEngineEnded(reason);
                break;

            case PollTick(var generation):
                HandlePollTick(generation);
                break;

            case PollCompleted(var generation, var snapshot, var error):
                HandlePollCompleted(generation, snapshot, error);
                break;
        }

        PublishState();
        message.Completion?.TrySetResult();
        if (notify is not null) OnSnapshotChanged?.Invoke(notify);
        return ValueTask.CompletedTask;
    }

    private void OnMessageError(Exception e, Message message)
    {
        Plugin.Log.Error(e, "DirectoryPlayer message failed: {message}", message);
        message.Completion?.TrySetException(e);
    }

    private void ApplyRescan(string[] files)
    {
        var current = playlist is null or { Length: 0 } ? null : playlist[index];
        originalFiles = files;
        playlist = shuffle ? [.. files.Shuffle()] : files;
        var newIndex = current is null ? -1 : Array.IndexOf(playlist, current);
        index = newIndex >= 0 ? newIndex : 0;
    }

    private void ToggleShuffleCore()
    {
        if (playlist is null or { Length: 0 }) return;
        var current = playlist[index];
        playlist = shuffle ? originalFiles! : [.. playlist.Shuffle()];
        shuffle = !shuffle;
        index = Array.IndexOf(playlist, current);
    }

    private void HandleEngineEnded(EndReason reason)
    {
        if (!playing) return;

        if (reason == EndReason.Failed)
        {
            consecutiveFailures++;
            var count = playlist?.Length ?? 0;
            if (count == 0 || consecutiveFailures >= count)
            {
                Plugin.Log.Warning(
                    $"DirectoryPlayer: no playable tracks ({consecutiveFailures} consecutive load failures); stopping.");
                playing = false;
                consecutiveFailures = 0;
                engineCommands.Stop();
                return;
            }
        }
        else
        {
            consecutiveFailures = 0;
        }

        Advance();
    }

    private void PlayCurrent()
    {
        if (playlist is null or { Length: 0 }) return;
        position = null;
        engineCommands.Load(playlist[index], TimeSpan.Zero, true);
    }

    private void Advance()
    {
        if (playlist is null or { Length: 0 }) return;
        index++;
        if (index >= playlist.Length) index = 0;
        PlayCurrent();
    }

    private void HandlePollTick(long generation)
    {
        if (generation != pollGeneration) return;
        pollDelayCts?.Dispose();
        pollDelayCts = null;
        pollDelayTask = null;

        if (state != PlaybackState.Playing)
        {
            SchedulePoll(pollIdle);
            return;
        }

        pollTask = PollEngine(generation, asyncCts.Token);
    }

    private async Task PollEngine(long generation, CancellationToken token)
    {
        EngineSnapshot? snapshot = null;
        Exception? error = null;
        try
        {
            snapshot = await player.GetStateAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
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
            Plugin.Log.Error(error, "DirectoryPlayer: position poll failed");
            SchedulePoll(errorBackoff);
            return;
        }

        if (snapshot is not null) position = snapshot.Position;
        SchedulePoll(state == PlaybackState.Playing ? pollPlaying : pollIdle);
    }

    private void SchedulePoll(TimeSpan delay)
    {
        var generation = ++pollGeneration;
        pollDelayCts = CancellationTokenSource.CreateLinkedTokenSource(asyncCts.Token);
        pollDelayTask = PostPollAfterDelay(delay, generation, pollDelayCts.Token);
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

    private void PublishState()
    {
        IReadOnlyList<string> tracks = playlist ?? [];
        var current = playlist is null or { Length: 0 } ? null : playlist[index];
        var next = playlist is null or { Length: 0 } ? null : playlist[(index + 1) % playlist.Length];
        published = new DirectoryPlayerView(tracks, index, shuffle, position, state, current, next);
    }

    private string[] ScanFiles() => [
        .. Extensions
           .SelectMany(pattern =>
               System.IO.Directory.EnumerateFiles(Directory, pattern, SearchOption.AllDirectories))
           .OrderBy(path => path, NaturalPathComparer.Instance)
    ];

    private void OnEngineChanged(object? _, EngineSnapshot snapshot)
        => mailbox.TryPost(new EngineChanged(snapshot));

    private void OnEngineEnded(object? _, EndReason reason)
        => mailbox.TryPost(new EngineEnded(reason));

    private async ValueTask OnCompleted()
    {
        asyncCts.Cancel();
        pollDelayCts?.Cancel();
        if (pollDelayTask is not null) await pollDelayTask;
        if (pollTask is not null) await pollTask;
        pollDelayCts?.Dispose();
        pollDelayCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        player.OnPlaybackEnded -= OnEngineEnded;
        player.OnChanged -= OnEngineChanged;

        await mailbox.DisposeAsync();

        try
        {
            await engineCommands.DisposeAsync().AsTask().WaitAsync(disposeTimeout);
        }
        catch (TimeoutException)
        {
            Plugin.Log.Debug("DirectoryPlayer: engine command drain timed out on dispose");
        }

        // Nothing else is guaranteed to stop a replaced player's audio host.
        try
        {
            using var timeout = new CancellationTokenSource(disposeTimeout);
            await player.StopAsync(timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception e)
        {
            Plugin.Log.Debug($"DirectoryPlayer: stop-on-dispose failed: {e.Message}");
        }

        asyncCts.Dispose();
    }
}
