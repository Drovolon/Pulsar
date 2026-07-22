using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Broadcast;
using Pulsar.Broadcast.Local;
using Pulsar.Playback;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// LocalSource is the folder IMusicSource: its Current null-collapse decides
/// "broadcasting or not" for the whole manifest pipeline.
/// </summary>
public class LocalSourceTests : IAsyncLifetime
{
    private sealed class MutableCatalogLoader(string root, TrackCatalog catalog) : ITrackCatalogLoader
    {
        public string RootDirectory { get; } = root;
        public TrackCatalog Catalog { get; set; } = catalog;
        public Task<TrackCatalog> LoadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Catalog);
    }

    private sealed class SequencedCatalogLoader(
        string root,
        TrackCatalog initial,
        Task<TrackCatalog> firstRescan,
        Task<TrackCatalog> secondRescan) : ITrackCatalogLoader
    {
        private int calls;
        public string RootDirectory { get; } = root;
        public int Calls => Volatile.Read(ref calls);

        public Task<TrackCatalog> LoadAsync(CancellationToken cancellationToken = default)
            => Interlocked.Increment(ref calls) switch
            {
                1 => Task.FromResult(initial),
                2 => firstRescan,
                3 => secondRescan,
                _ => throw new InvalidOperationException("Unexpected catalog load"),
            };
    }

    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-local-source-test-");
    private readonly FakeRemoteEngine engine = new();
    private readonly EngineSession engineSession;
    private LocalSource? source;

    public LocalSourceTests() => engineSession = new EngineSession(engine);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (source is not null) await source.DisposeAsync();
        await engineSession.DisposeAsync();
        dir.Delete(recursive: true);
    }

    private async Task<LocalSource> Create(params string[] tracks)
    {
        foreach (var t in tracks) TestData.CreateTrack(dir, t);
        var loader = new FolderTrackCatalogLoader(dir.FullName);
        var catalog = await loader.LoadAsync();
        source = await LocalSource.Create(engineSession, loader, catalog);
        return source;
    }

    [Fact]
    public async Task Stopped_source_is_not_broadcasting()
    {
        var local = await Create("a.mp3");
        Assert.Null(local.Current); // null == "not broadcasting", the documented contract
    }

    [Fact]
    public async Task Playing_snapshot_carries_the_track_and_meta()
    {
        var local = await Create("a.mp3");
        local.Play();
        await TestWait.Assert(() => local.Current is { IsPlaying: true }, "snapshot goes live");
        var snap = local.Current!;
        Assert.Equal(local.CurrentTrack!.FilePath, snap.FilePath);
        Assert.Equal("a.mp3", snap.Meta?.OriginalFileName);
    }

    [Fact]
    public async Task Paused_source_still_has_a_snapshot()
    {
        var local = await Create("a.mp3");
        local.Play();
        await TestWait.Assert(() => local.Current is { IsPlaying: true }, "playing");
        local.Pause();
        // Paused is NOT a stop: the snapshot holds (syncs as Paused).
        await TestWait.Assert(() => local.Current is { IsPlaying: false }, "paused snapshot holds");
    }

    [Fact]
    public async Task Player_events_forward_through_the_source()
    {
        var local = await Create("a.mp3");
        var events = 0;
        local.OnSnapshotChanged += _ => Interlocked.Increment(ref events);
        local.Play();
        await TestWait.Assert(() => events > 0, "snapshot change forwards");
    }

    [Fact]
    public async Task Dispose_stops_engine_playback()
    {
        var local = await Create("a.mp3");
        local.Play();
        await TestWait.Assert(() => engine.Snapshot.State == PlaybackState.Playing, "playing");
        await local.DisposeAsync();
        source = null;
        Assert.Contains("Stop", engine.Ops);
    }

    [Fact]
    public async Task Group_selection_replaces_the_queue_and_rescan_falls_back_when_group_disappears()
    {
        TestData.CreateTrack(dir, "one.scd");
        TestData.CreateTrack(dir, "two.scd");
        var one = new LocalTrack(Path.Combine(dir.FullName, "one.scd"), "one.scd", "One");
        var two = new LocalTrack(Path.Combine(dir.FullName, "two.scd"), "two.scd", "Two");
        var all = new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [one, two]);
        var rock = new TrackGroup("group_rock.json", "Rock", [two]);
        var loader = new MutableCatalogLoader(dir.FullName, new TrackCatalog([all, rock]));
        source = await LocalSource.Create(
            engineSession, loader, loader.Catalog, "CoolMod");

        await source.SelectGroup(rock.Id);
        Assert.Equal(rock.Id, source.SelectedGroup.Id);
        Assert.Single(source.Tracks);
        Assert.Equal(two.FilePath, source.Tracks[0].FilePath);

        loader.Catalog = new TrackCatalog([all]);
        await source.Rescan();

        Assert.Equal(TrackCatalog.AllFilesId, source.SelectedGroup.Id);
        Assert.Equal(2, source.Tracks.Count);
    }

    [Fact]
    public async Task Throwing_queue_subscriber_cannot_leave_group_selection_half_committed()
    {
        var one = new LocalTrack(Path.Combine(dir.FullName, "one.scd"), "one.scd", "One");
        var two = new LocalTrack(Path.Combine(dir.FullName, "two.scd"), "two.scd", "Two");
        var all = new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [one, two]);
        var rock = new TrackGroup("group_rock.json", "Rock", [two]);
        var loader = new MutableCatalogLoader(dir.FullName, new TrackCatalog([all, rock]));
        source = await LocalSource.Create(
            engineSession, loader, loader.Catalog, "CoolMod");
        source.OnQueueChanged += () => throw new InvalidOperationException("subscriber failed");

        await source.SelectGroup(rock.Id);

        Assert.Equal(rock.Id, source.SelectedGroup.Id);
        Assert.Single(source.Tracks);
        Assert.Equal(two.FilePath, source.Tracks[0].FilePath);
    }

    [Fact]
    public async Task Overlapping_rescans_only_commit_the_latest_result()
    {
        var initialTrack = new LocalTrack(Path.Combine(dir.FullName, "initial.scd"), "initial.scd", "Initial");
        var oldTrack = new LocalTrack(Path.Combine(dir.FullName, "old.scd"), "old.scd", "Old");
        var newTrack = new LocalTrack(Path.Combine(dir.FullName, "new.scd"), "new.scd", "New");
        TrackCatalog Catalog(LocalTrack track) => new([new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [track])]);

        var olderResult = new TaskCompletionSource<TrackCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newerResult = new TaskCompletionSource<TrackCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loader = new SequencedCatalogLoader(
            dir.FullName, Catalog(initialTrack), olderResult.Task, newerResult.Task);
        var initial = await loader.LoadAsync();
        source = await LocalSource.Create(engineSession, loader, initial);

        var olderRescan = source.Rescan();
        await TestWait.Assert(() => loader.Calls == 2, "the older rescan starts");
        var newerRescan = source.Rescan();
        await TestWait.Assert(() => loader.Calls == 3, "the newer rescan starts independently");

        olderResult.TrySetResult(Catalog(oldTrack));
        await TestWait.Within(olderRescan, "the obsolete rescan completes");
        Assert.Equal(initialTrack.FilePath, source.Tracks[0].FilePath);

        newerResult.TrySetResult(Catalog(newTrack));
        await TestWait.Within(newerRescan, "the newer rescan applies");

        Assert.Equal(newTrack.FilePath, source.View.Catalog.AllFiles.Tracks[0].FilePath);
        Assert.Equal(newTrack.FilePath, source.Tracks[0].FilePath);
    }
}
