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

public sealed record LocalBrowserView(
    TrackCatalog Catalog,
    TrackGroup SelectedGroup)
{
    public bool SelectedGroupIsActive { get; init; }
}

public sealed record LocalQueueView(
    IReadOnlyList<QueueEntry> Entries,
    IReadOnlyList<UpcomingTrack> Upcoming,
    PlaybackPosition? Position,
    PlaybackState State,
    LocalTrack? CurrentTrack,
    LocalTrack? NextTrack);

/// <summary>The complete immutable view of the browser and playback queue.</summary>
public sealed record LocalSourceView(LocalBrowserView Browser, LocalQueueView Queue);

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
    private sealed record BrowserReplaced(
        ITrackCatalogLoader Loader,
        TrackCatalog Catalog,
        string? ModDirectoryName,
        string? SelectedGroupId,
        TaskCompletionSource Done) : Message(Done);
    private sealed record PlayNowRequested(LocalTrack Track, TrackGroup Source) : Message;
    private sealed record ActiveSourceSet : Message;
    private sealed record ActiveSourceTrackPlayed(LocalTrack Track) : Message;
    private sealed record QueueEntryPlayed(QueueEntryId Id) : Message;
    private sealed record AddNextRequested(LocalTrack Track) : Message;
    private sealed record AddToEndRequested(LocalTrack Track) : Message;
    private sealed record RemoveRequested(QueueEntryId Id) : Message;
    private sealed record MoveRequested(QueueEntryId Id, int Offset) : Message;
    private sealed record ClearQueueRequested : Message;
    private sealed record ShuffleUpcomingRequested : Message;
    private sealed record PlayRequested : Message;
    private sealed record StopRequested : Message;
    private sealed record NextRequested : Message;
    private sealed record PreviousRequested : Message;
    private sealed record PauseRequested : Message;
    private sealed record ResumeRequested : Message;
    private sealed record SessionObserved(EngineObservation Value) : Message;
    private sealed record SessionEnded(EngineSessionEnded Value) : Message;
    private sealed record SessionReconnected : Message;
    private sealed record Barrier(TaskCompletionSource Done) : Message(Done);

    private ITrackCatalogLoader loader;
    private readonly EngineSession engine;
    private readonly Playlist playlist;
    private readonly SerializedMailbox<Message> mailbox;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly Lazy<Task> disposeTask;
    private readonly TimeSpan disposeTimeout;

    // State owned by the mailbox
    private TrackCatalog catalog;
    private string selectedId;
    private TrackGroup? activeGroupSnapshot;
    private long latestCatalogRequest;
    private volatile LocalSourceView published;
    private volatile SourceFrame? publishedFrame;

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
        playlist = new Playlist();
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

    internal static LocalSource CreateEmpty(EngineSession engine)
    {
        var loader = new EmptyCatalogLoader();
        var catalog = new TrackCatalog(
            [new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [])]);
        return new LocalSource(
            engine, loader, catalog, null, TrackCatalog.AllFilesId,
            TimeSpan.FromSeconds(2));
    }

    public LocalSourceView View => published;
    public TrackGroup SelectedGroup => View.Browser.SelectedGroup;
    public IReadOnlyList<QueueEntry> Queue => View.Queue.Entries;
    public IReadOnlyList<UpcomingTrack> UpNext => View.Queue.Upcoming;
    public PlaybackPosition? Position => View.Queue.Position;
    public PlaybackState State => View.Queue.State;
    public LocalTrack? CurrentTrack => View.Queue.CurrentTrack;
    public LocalTrack? NextTrack => View.Queue.NextTrack;
    public string RootDirectory => loader.RootDirectory;
    public string? ModDirectoryName { get; private set; }
    public IReadOnlyList<LocalTrack> Library => View.Browser.SelectedGroup.Tracks;

    public event Action<LocalSourceView>? OnPlaybackChanged;
    internal event Action? OnQueueChanged;
    public event Action<SourceSnapshot?>? OnSnapshotChanged;
    public event Action? OnChanged;

    public void PlayNow(LocalTrack track)
    {
        var source = View.Browser.SelectedGroup;
        mailbox.TryPost(new PlayNowRequested(track, source));
    }
    public void SetActiveSource() => mailbox.TryPost(new ActiveSourceSet());
    public void Play(QueueEntryId id) => mailbox.TryPost(new QueueEntryPlayed(id));
    public void PlayFromActiveSource(LocalTrack track)
        => mailbox.TryPost(new ActiveSourceTrackPlayed(track));
    public void AddNext(LocalTrack track) => mailbox.TryPost(new AddNextRequested(track));
    public void AddToEnd(LocalTrack track) => mailbox.TryPost(new AddToEndRequested(track));
    public void Remove(QueueEntryId id) => mailbox.TryPost(new RemoveRequested(id));
    public void Move(QueueEntryId id, int offset) => mailbox.TryPost(new MoveRequested(id, offset));
    public void ClearQueue() => mailbox.TryPost(new ClearQueueRequested());
    public void ShuffleUpcoming() => mailbox.TryPost(new ShuffleUpcomingRequested());
    public void Play() => mailbox.TryPost(new PlayRequested());
    public void Stop() => mailbox.TryPost(new StopRequested());
    public void Next() => mailbox.TryPost(new NextRequested());
    public void Prev() => mailbox.TryPost(new PreviousRequested());
    public void Pause() => mailbox.TryPost(new PauseRequested());
    public void Resume() => mailbox.TryPost(new ResumeRequested());
    public void Volume(float value) => engine.SetVolume(value);
    public void Seek(TimeSpan value) => engine.Seek(value);

    public Task SelectGroup(string? groupId)
        => PostWithCompletion(done => new GroupSelected(groupId, done));

    internal Task ReplaceBrowserAsync(
        ITrackCatalogLoader replacementLoader,
        TrackCatalog replacementCatalog,
        string? modDirectoryName,
        string? selectedGroupId)
        => PostWithCompletion(done => new BrowserReplaced(
            replacementLoader, replacementCatalog, modDirectoryName, selectedGroupId, done));

    public Task Rescan()
    {
        var completion = NewCompletion();
        var request = Interlocked.Increment(ref latestCatalogRequest);
        _ = LoadCatalog(request, completion);
        return completion.Task;
    }

    internal Task DrainForTests() => PostWithCompletion(done => new Barrier(done));

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
        var observationAccepted = false;
        var queueChanged = false;
        var targetChanged = false;
        switch (message)
        {
            case GroupSelected(var groupId, _):
                {
                    var group = catalog.FindGroup(groupId);
                    if (group.Id == selectedId) break;
                    selectedId = group.Id;
                    break;
                }
            case CatalogReplaced(var request, var replacement, _):
                if (request != Volatile.Read(ref latestCatalogRequest)) break;
                catalog = replacement;
                var selected = catalog.FindGroup(selectedId);
                selectedId = selected.Id;
                break;
            case BrowserReplaced(var replacementLoader, var replacementCatalog,
                                 var modDirectoryName, var selectedGroupId, _):
                loader = replacementLoader;
                catalog = replacementCatalog;
                selectedId = catalog.FindGroup(selectedGroupId).Id;
                ModDirectoryName = modDirectoryName;
                Interlocked.Increment(ref latestCatalogRequest);
                break;
            case PlayNowRequested(var track, var source):
                targetChanged = playlist.PlaySource(source.Tracks, track);
                activeGroupSnapshot = source;
                queueChanged = true;
                break;
            case ActiveSourceSet:
                activeGroupSnapshot = catalog.FindGroup(selectedId);
                playlist.SetSource(activeGroupSnapshot.Tracks);
                queueChanged = true;
                break;
            case QueueEntryPlayed(var id):
                targetChanged = playlist.Play(id);
                queueChanged = targetChanged;
                break;
            case ActiveSourceTrackPlayed(var track):
                targetChanged = playlist.PlaySourceTrack(track);
                queueChanged = targetChanged;
                break;
            case AddNextRequested(var track):
                playlist.AddNext(track);
                queueChanged = true;
                break;
            case AddToEndRequested(var track):
                playlist.AddToEnd(track);
                queueChanged = true;
                break;
            case RemoveRequested(var id):
                playlist.Remove(id);
                queueChanged = true;
                break;
            case MoveRequested(var id, var offset):
                playlist.Move(id, offset);
                queueChanged = true;
                break;
            case ClearQueueRequested:
                playlist.ClearQueue();
                queueChanged = true;
                break;
            case ShuffleUpcomingRequested:
                playlist.ShuffleUpcoming();
                queueChanged = true;
                break;
            case PlayRequested:
                if (!playlist.HasActiveSource)
                {
                    var group = catalog.FindGroup(selectedId);
                    targetChanged = group.Tracks.Count > 0
                        ? playlist.PlaySource(group.Tracks, group.Tracks[0])
                        : playlist.Play();
                    if (group.Tracks.Count > 0)
                        activeGroupSnapshot = group;
                    queueChanged = targetChanged;
                }
                else
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
            case PauseRequested:
                targetChanged = playlist.Pause();
                break;
            case ResumeRequested:
                targetChanged = playlist.Resume();
                break;
            case SessionObserved(var observation):
                var observed = playlist.Observe(observation);
                observationAccepted = observed.Accepted;
                playbackChanged = observed.CursorChanged;
                break;
            case SessionEnded(var ended):
                var transition = playlist.End(ended);
                playbackChanged = transition.Notify;
                targetChanged = transition.TargetChanged;
                queueChanged = transition.QueueChanged;
                break;
            case SessionReconnected:
                targetChanged = true;
                break;
            case Barrier:
                break;
        }

        if (targetChanged) engine.SetTarget(playlist.Target);
        published = BuildView();

        var frameChanged = false;
        if (observationAccepted)
        {
            var snapshot = SnapshotFrom(published, message is SessionObserved(var value)
                ? value.Snapshot.ObservedAt
                : DateTimeOffset.UtcNow);
            var cursor = playbackChanged || publishedFrame is null
                ? new SourceCursor()
                : publishedFrame.Cursor;
            publishedFrame = snapshot is null ? null : new SourceFrame(cursor, snapshot);
            frameChanged = playbackChanged;
        }
        else if (playbackChanged)
        {
            var snapshot = SnapshotFrom(published, publishedFrame?.Snapshot.AsOf ?? DateTimeOffset.UtcNow);
            publishedFrame = snapshot is null
                ? null
                : new SourceFrame(new SourceCursor(), snapshot);
            frameChanged = true;
        }

        // Keep the current track visible while its successor loads, but update
        // the advertised next track when the queue changes.
        if (!playbackChanged && publishedFrame is { } held && (queueChanged || targetChanged))
        {
            var next = ProjectNext(held.Snapshot.FilePath, published);
            if (held.Snapshot.NextFilePath != next)
            {
                publishedFrame = held with { Snapshot = held.Snapshot with { NextFilePath = next } };
                frameChanged = true;
            }
        }
        try
        {
            if (playbackChanged)
            {
                OnPlaybackChanged?.Invoke(published);
                OnSnapshotChanged?.Invoke(Current);
            }
            if (queueChanged) OnQueueChanged?.Invoke();
            if (frameChanged) OnChanged?.Invoke();
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
        var selectedGroup = catalog.FindGroup(selectedId);
        return new LocalSourceView(
            new LocalBrowserView(catalog, selectedGroup)
            {
                SelectedGroupIsActive = ReferenceEquals(selectedGroup, activeGroupSnapshot),
            },
            new LocalQueueView(
                view.Queue,
                view.Upcoming,
                view.Position,
                view.State,
                view.CurrentTrack,
                view.NextTrack));
    }

    public SourceSnapshot? Current => publishedFrame?.Snapshot;
    public SourceFrame? Frame => publishedFrame;

    private SourceSnapshot? SnapshotFrom(LocalSourceView view, DateTimeOffset observedAt)
    {
        if (view.Queue.State == PlaybackState.Stopped || view.Queue.CurrentTrack is null) return null;
        return new SourceSnapshot(
            view.Queue.CurrentTrack.FilePath,
            ProjectNext(view.Queue.CurrentTrack.FilePath, view),
            view.Queue.State == PlaybackState.Playing,
            view.Queue.Position?.Current ?? TimeSpan.Zero,
            observedAt,
            new TrackMeta
            {
                DisplayName = view.Queue.CurrentTrack.DisplayName,
                OriginalFileName = Path.GetFileName(view.Queue.CurrentTrack.FilePath),
                DurationMs = (long)(view.Queue.Position?.Total.TotalMilliseconds ?? 0),
            });
    }

    private string? ProjectNext(string currentPath, LocalSourceView view)
    {
        var target = playlist.Target?.Path;
        if (target is not null
            && (!playlist.TargetIsConfirmed
                || !string.Equals(target, currentPath, StringComparison.OrdinalIgnoreCase)))
            return target;
        return view.Queue.NextTrack?.FilePath;
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

    private sealed class EmptyCatalogLoader : ITrackCatalogLoader
    {
        public string RootDirectory => "";
        public Task<TrackCatalog> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new TrackCatalog(
                [new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [])]));
    }
}
