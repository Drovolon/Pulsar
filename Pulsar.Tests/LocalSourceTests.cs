using System;
using System.IO;
using System.Linq;
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
    public async Task Playing_snapshot_carries_the_catalog_display_name_separately_from_the_filename()
    {
        var path = TestData.CreateTrack(dir, "2826_DeathbyRomy - Vicious Bliss_2842.scd");
        var track = new LocalTrack(
            path,
            Path.GetFileName(path),
            "DeathbyRomy - Vicious Bliss");
        var catalog = new TrackCatalog(
            [new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [track])]);
        var loader = new MutableCatalogLoader(dir.FullName, catalog);
        source = await LocalSource.Create(engineSession, loader, catalog);

        source.Play();
        await TestWait.Assert(() => source.Current is { IsPlaying: true }, "snapshot goes live");

        Assert.Equal("DeathbyRomy - Vicious Bliss", source.Current!.Meta.DisplayName);
        Assert.Equal("2826_DeathbyRomy - Vicious Bliss_2842.scd", source.Current.Meta.OriginalFileName);
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
    public async Task Queue_projection_changes_preserve_cursor_and_authoritative_anchor()
    {
        var local = await Create("a.mp3", "b.mp3", "queued.mp3");
        local.PlayNow(local.Library.Single(track => track.RelativePath == "a.mp3"));
        await TestWait.Assert(() => local.Frame is not null, "playing frame");
        var before = local.Frame!;
        var queued = local.Library.Single(track => track.RelativePath == "queued.mp3");

        local.AddNext(queued);
        await local.DrainForTests();

        var after = local.Frame!;
        Assert.Same(before.Cursor, after.Cursor);
        Assert.Equal(before.Snapshot.Position, after.Snapshot.Position);
        Assert.Equal(before.Snapshot.AsOf, after.Snapshot.AsOf);
        Assert.Equal(queued.FilePath, after.Snapshot.NextFilePath);
    }

    [Fact]
    public async Task Natural_end_holds_the_listener_frame_while_queued_successor_loads()
    {
        var local = await Create("a.mp3", "b.mp3", "queued.mp3");
        local.PlayNow(local.Library.Single(track => track.RelativePath == "a.mp3"));
        await TestWait.Assert(() => local.Frame is not null, "playing frame");
        var queued = local.Library.Single(track => track.RelativePath == "queued.mp3");
        local.AddNext(queued);
        await local.DrainForTests();
        var before = local.Frame!;

        engine.DeferLoads = true;
        engine.FinishTrack();
        await TestWait.Assert(
            () => engine.Calls.Count(call => call.Op == "Load") >= 2,
            "queued successor load dispatched");

        Assert.NotNull(local.Frame);
        Assert.Same(before.Cursor, local.Frame!.Cursor);
        Assert.Equal(before.Snapshot.FilePath, local.Frame.Snapshot.FilePath);
        Assert.Equal(queued.FilePath, local.Frame.Snapshot.NextFilePath);

        engine.CompleteDeferredLoad();
        await TestWait.Assert(() => local.Current?.FilePath == queued.FilePath, "queued track confirms");
        Assert.NotSame(before.Cursor, local.Frame!.Cursor);
    }

    [Fact]
    public async Task Poll_confirmed_seek_mints_a_cursor_when_the_engine_event_was_missed()
    {
        var local = await Create("a.mp3");
        local.Play();
        await TestWait.Assert(() => local.Frame is not null, "playing frame");
        var before = local.Frame!;
        engine.SuppressUpdatedEvents = true;

        local.Seek(TimeSpan.FromMinutes(1));

        await TestWait.Assert(
            () => local.Frame is { } frame
                  && !ReferenceEquals(frame.Cursor, before.Cursor)
                  && frame.Snapshot.Position >= TimeSpan.FromMinutes(1),
            "poll observes the missed seek as a cursor transition");
    }

    [Fact]
    public async Task Same_path_queued_occurrence_remains_the_projected_successor_while_loading()
    {
        var local = await Create("a.mp3", "b.mp3");
        var a = local.Library.Single(track => track.RelativePath == "a.mp3");
        var b = local.Library.Single(track => track.RelativePath == "b.mp3");
        local.PlayNow(a);
        await TestWait.Assert(() => local.Frame is not null, "playing frame");
        local.AddToEnd(a);
        local.AddToEnd(b);
        await local.DrainForTests();
        Assert.Equal(a.FilePath, local.Frame!.Snapshot.NextFilePath);

        engine.DeferLoads = true;
        engine.FinishTrack();
        await TestWait.Assert(
            () => engine.Calls.Count(call => call.Op == "Load") >= 2,
            "same-path successor load dispatched");

        Assert.Equal(a.FilePath, local.Frame!.Snapshot.NextFilePath);
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
    public async Task Group_selection_only_changes_the_browser_and_rescan_falls_back_when_group_disappears()
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
        source.PlayNow(one);
        await source.DrainForTests();

        await source.SelectGroup(rock.Id);
        Assert.Equal(rock.Id, source.SelectedGroup.Id);
        Assert.Single(source.Library);
        Assert.Equal(two.FilePath, source.Library[0].FilePath);
        Assert.Empty(source.Queue);
        Assert.Equal(one.FilePath, source.CurrentTrack?.FilePath); // active source is independent
        Assert.Equal(two.FilePath, Assert.Single(source.UpNext).Track.FilePath);

        loader.Catalog = new TrackCatalog([all]);
        await source.Rescan();

        Assert.Equal(TrackCatalog.AllFilesId, source.SelectedGroup.Id);
        Assert.Equal(2, source.Library.Count);
        Assert.Equal(one.FilePath, source.CurrentTrack?.FilePath);
    }

    [Fact]
    public async Task Group_selection_does_not_notify_or_mutate_the_playback_queue()
    {
        var one = new LocalTrack(Path.Combine(dir.FullName, "one.scd"), "one.scd", "One");
        var two = new LocalTrack(Path.Combine(dir.FullName, "two.scd"), "two.scd", "Two");
        var all = new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [one, two]);
        var rock = new TrackGroup("group_rock.json", "Rock", [two]);
        var loader = new MutableCatalogLoader(dir.FullName, new TrackCatalog([all, rock]));
        source = await LocalSource.Create(
            engineSession, loader, loader.Catalog, "CoolMod");
        source.AddToEnd(one);
        await source.DrainForTests();
        source.OnQueueChanged += () => throw new InvalidOperationException("subscriber failed");

        await source.SelectGroup(rock.Id);

        Assert.Equal(rock.Id, source.SelectedGroup.Id);
        Assert.Single(source.Library);
        Assert.Equal(two.FilePath, source.Library[0].FilePath);
        Assert.Equal(one, Assert.Single(source.Queue).Track);
    }

    [Fact]
    public async Task Setting_selected_group_as_active_preserves_current_playback_and_manual_queue()
    {
        var one = new LocalTrack(Path.Combine(dir.FullName, "one.scd"), "one.scd", "One");
        var two = new LocalTrack(Path.Combine(dir.FullName, "two.scd"), "two.scd", "Two");
        var three = new LocalTrack(Path.Combine(dir.FullName, "three.scd"), "three.scd", "Three");
        var queued = new LocalTrack(Path.Combine(dir.FullName, "queued.scd"), "queued.scd", "Queued");
        foreach (var track in new[] { one, two, three, queued })
            TestData.CreateTrack(dir, track.RelativePath);
        var all = new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [one, two, three, queued]);
        var rock = new TrackGroup("group_rock.json", "Rock", [two, three]);
        var loader = new MutableCatalogLoader(dir.FullName, new TrackCatalog([all, rock]));
        source = await LocalSource.Create(engineSession, loader, loader.Catalog, "CoolMod");
        source.PlayNow(one);
        await TestWait.Assert(() => engine.Snapshot.Path == one.FilePath, "first source track plays");
        source.AddToEnd(queued);
        await source.SelectGroup(rock.Id);
        var loadsBefore = engine.Calls.Count(call => call.Op == "Load");

        source.SetActiveSource();
        await source.DrainForTests();

        Assert.Equal(one.FilePath, source.CurrentTrack?.FilePath);
        Assert.Equal(loadsBefore, engine.Calls.Count(call => call.Op == "Load"));
        Assert.Equal(queued, Assert.Single(source.Queue).Track);
        Assert.Equal(
            [queued.FilePath, two.FilePath, three.FilePath],
            source.UpNext.Select(item => item.Track.FilePath));

        engine.FinishTrack();
        await TestWait.Assert(() => engine.Snapshot.Path == queued.FilePath, "manual queue plays next");
        engine.FinishTrack();
        await TestWait.Assert(() => engine.Snapshot.Path == two.FilePath, "replacement source follows queue");
    }

    [Fact]
    public async Task Setting_selected_group_as_active_while_stopped_changes_the_next_play_source()
    {
        var one = new LocalTrack(Path.Combine(dir.FullName, "one.scd"), "one.scd", "One");
        var two = new LocalTrack(Path.Combine(dir.FullName, "two.scd"), "two.scd", "Two");
        TestData.CreateTrack(dir, one.RelativePath);
        TestData.CreateTrack(dir, two.RelativePath);
        var all = new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [one, two]);
        var rock = new TrackGroup("group_rock.json", "Rock", [two]);
        var loader = new MutableCatalogLoader(dir.FullName, new TrackCatalog([all, rock]));
        source = await LocalSource.Create(engineSession, loader, loader.Catalog, "CoolMod");
        await source.SelectGroup(rock.Id);

        source.SetActiveSource();
        await source.DrainForTests();
        source.Play();

        await TestWait.Assert(() => engine.Snapshot.Path == two.FilePath, "replacement source starts");
    }

    [Fact]
    public async Task Selected_group_is_active_uses_the_source_snapshot_identity()
    {
        var one = new LocalTrack(Path.Combine(dir.FullName, "one.scd"), "one.scd", "One");
        var two = new LocalTrack(Path.Combine(dir.FullName, "two.scd"), "two.scd", "Two");
        TestData.CreateTrack(dir, one.RelativePath);
        TestData.CreateTrack(dir, two.RelativePath);
        var all = new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [one, two]);
        var rock = new TrackGroup("group_rock.json", "Rock", [one, two]);
        var loader = new MutableCatalogLoader(dir.FullName, new TrackCatalog([all, rock]));
        source = await LocalSource.Create(engineSession, loader, loader.Catalog, "CoolMod");

        Assert.False(source.View.Browser.SelectedGroupIsActive);
        source.PlayNow(one);
        await source.DrainForTests();
        Assert.True(source.View.Browser.SelectedGroupIsActive);

        await source.SelectGroup(rock.Id);
        Assert.False(source.View.Browser.SelectedGroupIsActive);
        source.SetActiveSource();
        await source.DrainForTests();
        Assert.True(source.View.Browser.SelectedGroupIsActive);

        source.ShuffleUpcoming();
        await source.DrainForTests();
        Assert.True(source.View.Browser.SelectedGroupIsActive);
        await source.SelectGroup(all.Id);
        Assert.False(source.View.Browser.SelectedGroupIsActive);
        await source.SelectGroup(rock.Id);
        Assert.True(source.View.Browser.SelectedGroupIsActive);

        loader.Catalog = new TrackCatalog([
            new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [one, two]),
            new TrackGroup(rock.Id, rock.Name, [one, two]),
        ]);
        await source.Rescan();

        Assert.False(source.View.Browser.SelectedGroupIsActive);
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
        source.PlayNow(initialTrack);
        await source.DrainForTests();

        var olderRescan = source.Rescan();
        await TestWait.Assert(() => loader.Calls == 2, "the older rescan starts");
        var newerRescan = source.Rescan();
        await TestWait.Assert(() => loader.Calls == 3, "the newer rescan starts independently");

        olderResult.TrySetResult(Catalog(oldTrack));
        await TestWait.Within(olderRescan, "the obsolete rescan completes");
        Assert.Equal(initialTrack.FilePath, source.CurrentTrack?.FilePath);

        newerResult.TrySetResult(Catalog(newTrack));
        await TestWait.Within(newerRescan, "the newer rescan applies");

        Assert.Equal(newTrack.FilePath, source.View.Browser.Catalog.AllFiles.Tracks[0].FilePath);
        Assert.Equal(newTrack.FilePath, source.Library[0].FilePath);
        Assert.Equal(initialTrack.FilePath, source.CurrentTrack?.FilePath);
    }
}
