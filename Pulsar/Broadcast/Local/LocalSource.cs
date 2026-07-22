using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Common.Api;
using Pulsar.Concurrency;
using Pulsar.Listening;
using Pulsar.Playback;

namespace Pulsar.Broadcast.Local;

/// <summary>The complete immutable view of local catalog and playback state.</summary>
public sealed record LocalSourceView(
    TrackCatalog Catalog,
    TrackGroup SelectedGroup,
    IReadOnlyList<LocalTrack> Tracks,
    int Index,
    bool Shuffle,
    PlaybackPosition? Position,
    PlaybackState State,
    LocalTrack? CurrentTrack,
    LocalTrack? NextTrack);

/// <summary>
/// Manages "local playback" - being the (audio) monitor for broadcast for folder or mods.
/// Owns catalog selection and Playlist. Publishes a LocalSourceView for use
/// in the UI (lock-free, thread-safe).
/// </summary>
public sealed class LocalSource : IMusicSource
{
    private abstract record Message(TaskCompletionSource? Completion = null);
    private sealed record GroupSelected(string? Id, TaskCompletionSource Done) : Message(Done);
    private sealed record CatalogReplaced(
        long Request, TrackCatalog Catalog, TaskCompletionSource Done) : Message(Done);
    private sealed record PlaylistReplaced(
        LocalTrack[] Tracks, TaskCompletionSource Done) : Message(Done);
    private sealed record PlayIndexRequested(int Value) : Message;
    private sealed record PlayRequested : Message;
    private sealed record StopRequested : Message;
    private sealed record NextRequested : Message;
    private sealed record PreviousRequested : Message;
    private sealed record ShuffleRequested(bool Value) : Message;
    private sealed record PauseRequested : Message;
    private sealed record ResumeRequested : Message;
    private sealed record SessionObserved(EngineObservation Value) : Message;
    private sealed record SessionEnded(EngineSessionEnded Value) : Message;
    private sealed record SessionReconnected : Message;

    private readonly ITrackCatalogLoader loader;
    private readonly EngineSession engine;
    private readonly Playlist playlist;
    private readonly SerializedMailbox<Message> mailbox;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly Lazy<Task> disposeTask;
    private readonly TimeSpan disposeTimeout;

    // State owned by the mailbox
    private TrackCatalog catalog;
    private string selectedId;
    private long latestCatalogRequest;
    private volatile LocalSourceView published;

    private LocalSource(
        EngineSession engine,
        ITrackCatalogLoader loader,
        TrackCatalog initialCatalog,
        string? modDirectoryName,
        string selectedGroupId,
        TimeSpan disposeTimeout)
    {
        this.engine = engine;
        this.loader = loader;
        catalog = initialCatalog;
        selectedId = selectedGroupId;
        playlist = new Playlist(catalog.FindGroup(selectedId).Tracks);
        ModDirectoryName = modDirectoryName;
        this.disposeTimeout = disposeTimeout;
        published = BuildView();
        mailbox = new SerializedMailbox<Message>(HandleMessage, OnMessageError);
        disposeTask = new Lazy<Task>(FinishDispose, LazyThreadSafetyMode.ExecutionAndPublication);
        engine.OnObserved += OnEngineObserved;
        engine.OnPlaybackEnded += OnEngineEnded;
        engine.OnReconnected += OnEngineReconnected;
    }

    internal static Task<LocalSource> Create(
        EngineSession engine,
        ITrackCatalogLoader loader,
        TrackCatalog initialCatalog,
        string? modDirectoryName = null,
        string? selectedGroupId = null,
        TimeSpan? disposeTimeout = null)
    {
        var selected = initialCatalog.FindGroup(selectedGroupId);
        return Task.FromResult(new LocalSource(
            engine,
            loader,
            initialCatalog,
            modDirectoryName,
            selected.Id,
            disposeTimeout ?? TimeSpan.FromSeconds(2)));
    }

    public LocalSourceView View => published;
    public TrackGroup SelectedGroup => View.SelectedGroup;
    public IReadOnlyList<LocalTrack> Tracks => View.Tracks;
    public int Index => View.Index;
    public bool Shuffle => View.Shuffle;
    public PlaybackPosition? Position => View.Position;
    public PlaybackState State => View.State;
    public LocalTrack? CurrentTrack => View.CurrentTrack;
    public LocalTrack? NextTrack => View.NextTrack;
    public string RootDirectory => loader.RootDirectory;
    public string? ModDirectoryName { get; }

    public event Action<LocalSourceView>? OnPlaybackChanged;
    internal event Action? OnQueueChanged;
    public event Action<SourceSnapshot?>? OnSnapshotChanged;

    public void PlayIndex(int value) => mailbox.TryPost(new PlayIndexRequested(value));
    public void Play() => mailbox.TryPost(new PlayRequested());
    public void Stop() => mailbox.TryPost(new StopRequested());
    public void Next() => mailbox.TryPost(new NextRequested());
    public void Prev() => mailbox.TryPost(new PreviousRequested());
    public void SetShuffle(bool value) => mailbox.TryPost(new ShuffleRequested(value));
    public void Pause() => mailbox.TryPost(new PauseRequested());
    public void Resume() => mailbox.TryPost(new ResumeRequested());
    public void Volume(float value) => engine.SetVolume(value);
    public void Seek(TimeSpan value) => engine.Seek(value);

    public Task SelectGroup(string? groupId)
        => PostWithCompletion(done => new GroupSelected(groupId, done));

    public Task ReplacePlaylistAsync(IReadOnlyList<LocalTrack> tracks)
        => PostWithCompletion(done => new PlaylistReplaced([.. tracks], done));

    public Task Rescan()
    {
        var completion = NewCompletion();
        var request = Interlocked.Increment(ref latestCatalogRequest);
        _ = LoadCatalog(request, completion);
        return completion.Task;
    }

    private Task PostWithCompletion(Func<TaskCompletionSource, Message> create)
    {
        var completion = NewCompletion();
        if (!mailbox.TryPost(create(completion)))
            completion.TrySetException(new ObjectDisposedException(nameof(LocalSource)));
        return completion.Task;
    }

    private async Task LoadCatalog(long request, TaskCompletionSource completion)
    {
        try
        {
            var replacement = await loader.LoadAsync(lifetimeCts.Token);
            if (!mailbox.TryPost(new CatalogReplaced(request, replacement, completion)))
                completion.TrySetException(new ObjectDisposedException(nameof(LocalSource)));
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
            completion.TrySetResult();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Local catalog rescan failed for {directory}", loader.RootDirectory);
            completion.TrySetResult();
        }
    }

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private ValueTask HandleMessage(Message message)
    {
        var playbackChanged = false;
        var queueChanged = false;
        var targetChanged = false;
        switch (message)
        {
            case GroupSelected(var groupId, _):
            {
                var group = catalog.FindGroup(groupId);
                if (group.Id == selectedId) break;
                selectedId = group.Id;
                targetChanged = playlist.Replace(group.Tracks);
                queueChanged = true;
                break;
            }
            case CatalogReplaced(var request, var replacement, _):
                if (request != Volatile.Read(ref latestCatalogRequest)) break;
                catalog = replacement;
                var selected = catalog.FindGroup(selectedId);
                selectedId = selected.Id;
                targetChanged = playlist.Replace(selected.Tracks);
                queueChanged = true;
                break;
            case PlaylistReplaced(var tracks, _):
                targetChanged = playlist.Replace(tracks);
                queueChanged = true;
                break;
            case PlayIndexRequested(var value):
                targetChanged = playlist.PlayIndex(value);
                break;
            case PlayRequested:
                targetChanged = playlist.Play();
                break;
            case StopRequested:
                targetChanged = playlist.Stop();
                break;
            case NextRequested:
                targetChanged = playlist.Next();
                break;
            case PreviousRequested:
                targetChanged = playlist.Previous();
                break;
            case ShuffleRequested(var value):
                if (playlist.View.Shuffle != value)
                {
                    playlist.SetShuffle(value);
                    queueChanged = true;
                }
                break;
            case PauseRequested:
                targetChanged = playlist.Pause();
                break;
            case ResumeRequested:
                targetChanged = playlist.Resume();
                break;
            case SessionObserved(var observation):
                playbackChanged = playlist.Observe(observation);
                break;
            case SessionEnded(var ended):
                var transition = playlist.End(ended);
                playbackChanged = transition.Notify;
                targetChanged = transition.TargetChanged;
                break;
            case SessionReconnected:
                targetChanged = true;
                break;
        }

        if (targetChanged) engine.SetTarget(playlist.Target);
        published = BuildView();
        try
        {
            if (playbackChanged)
            {
                OnPlaybackChanged?.Invoke(published);
                OnSnapshotChanged?.Invoke(SnapshotFrom(published));
            }
            if (queueChanged) OnQueueChanged?.Invoke();
        }
        finally
        {
            message.Completion?.TrySetResult();
        }
        return ValueTask.CompletedTask;
    }

    private LocalSourceView BuildView()
    {
        var view = playlist.View;
        return new LocalSourceView(
            catalog,
            catalog.FindGroup(selectedId),
            view.Tracks,
            view.Index,
            view.Shuffle,
            view.Position,
            view.State,
            view.CurrentTrack,
            view.NextTrack);
    }

    public SourceSnapshot? Current => SnapshotFrom(View);

    private static SourceSnapshot? SnapshotFrom(LocalSourceView view)
    {
        if (view.State == PlaybackState.Stopped || view.CurrentTrack is null) return null;
        return new SourceSnapshot(
            view.CurrentTrack.FilePath,
            view.NextTrack?.FilePath,
            view.State == PlaybackState.Playing,
            view.Position?.Current ?? TimeSpan.Zero,
            DateTimeOffset.UtcNow,
            new TrackMeta
            {
                OriginalFileName = Path.GetFileName(view.CurrentTrack.FilePath),
                DurationMs = (long)(view.Position?.Total.TotalMilliseconds ?? 0),
            });
    }

    private void OnEngineObserved(EngineObservation observation)
        => mailbox.TryPost(new SessionObserved(observation));

    private void OnEngineEnded(EngineSessionEnded ended)
        => mailbox.TryPost(new SessionEnded(ended));

    private void OnEngineReconnected() => mailbox.TryPost(new SessionReconnected());

    private void OnMessageError(Exception error, Message message)
    {
        Plugin.Log.Error(error, "LocalSource message failed: {message}", message);
        message.Completion?.TrySetException(error);
    }

    private async Task FinishDispose()
    {
        lifetimeCts.Cancel();
        engine.OnObserved -= OnEngineObserved;
        engine.OnPlaybackEnded -= OnEngineEnded;
        engine.OnReconnected -= OnEngineReconnected;
        await mailbox.DisposeAsync();
        try
        {
            await engine.StopAsync().WaitAsync(disposeTimeout);
        }
        catch (TimeoutException)
        {
            Plugin.Log.Debug("LocalSource: terminal stop is queued behind a slow engine command");
        }
        catch (Exception e)
        {
            Plugin.Log.Debug($"LocalSource: terminal stop failed: {e.Message}");
        }
        lifetimeCts.Dispose();
    }

    public ValueTask DisposeAsync() => new(disposeTask.Value);
}
