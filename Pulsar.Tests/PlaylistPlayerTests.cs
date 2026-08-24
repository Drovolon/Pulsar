using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.Broadcast;
using Pulsar.Broadcast.Local;
using Pulsar.Common.Api;
using Pulsar.Playback;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

public class PlaylistTests : IAsyncLifetime
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-dp-test-");
    private readonly FakeRemoteEngine engine = new();
    private readonly EngineSession engineSession;

    public PlaylistTests()
    {
        engineSession = new EngineSession(engine, TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(25),
                                          TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await engineSession.DisposeAsync();
        dir.Delete(true);
    }

    private void CreateTracks(params string[] names)
    {
        foreach (var name in names)
            TestData.CreateTrack(dir, name);
    }

    private async Task<LocalSource> CreatePlayer()
    {
        var loader = new FolderTrackCatalogLoader(dir.FullName);
        var catalog = await loader.LoadAsync();
        return await LocalSource.Create(engineSession, loader, catalog, disposeTimeout: TimeSpan.FromMilliseconds(200));
    }

    private Task<LocalSource> CreateFastPlayer() => CreatePlayer();

    private static LocalTrack[] QueueTracks(LocalSource player) => [.. player.Queue.Select(entry => entry.Track)];

    private static string[] UpNextFiles(LocalSource player) =>
        [.. player.UpNext.Select(item => Path.GetFileName(item.Track.FilePath))];

    private static int CurrentLibraryIndex(LocalSource player) =>
        player.CurrentTrack is { } current
            ? Enumerable.Range(0, player.Library.Count)
                        .FirstOrDefault(
                            index => string.Equals(player.Library[index].FilePath, current.FilePath,
                                                   StringComparison.OrdinalIgnoreCase), -1)
            : -1;

    private string[] LoadedFiles =>
        [.. engine.Calls.Where(c => c.Op == "Load").Select(c => Path.GetFileName((((string, TimeSpan, bool))c.Arg!).Item1))];

    private async Task WaitForLoads(int count) =>
        await TestWait.Assert(() => LoadedFiles.Length >= count, $"{count} loads dispatched");

    // Regression test: nothing but dispose ever issues a stop for a replaced
    // Nothing but source disposal stops the old target after a source switch.
    [Fact]
    public async Task DisposeAsync_stops_engine_playback()
    {
        CreateTracks("a.mp3");
        var player = await CreatePlayer();

        player.Play();
        Assert.True(await engine.WaitForCall("Load"), "engine never received the Load for Play()");

        await player.DisposeAsync();

        Assert.Contains("Stop", engine.Ops);
    }

    [Fact]
    public async Task Dispose_completes_even_if_the_host_stop_hangs()
    {
        // Stop-on-dispose is best-effort: a wedged host must not hang a source switch
        // or plugin unload forever. The small dispose timeout is the point here.
        CreateTracks("01.mp3");
        var player = await CreatePlayer();
        player.Play();
        await WaitForLoads(1);

        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stall = op => op == "Stop" ? releaseStop.Task : null;
        try
        {
            await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains("Stop", engine.Ops);
        } finally
        {
            engine.Stall = null;
            releaseStop.TrySetResult();
            await engineSession.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Dispose_completes_even_if_the_position_poll_hangs()
    {
        CreateTracks("01.mp3");
        var player = await CreateFastPlayer();
        var pollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stall = op =>
        {
            if (op != "GetState") return null;
            pollStarted.TrySetResult();
            return releasePoll.Task;
        };

        try
        {
            player.Play();
            await TestWait.Within(pollStarted.Task, "position poll starts");

            await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        } finally
        {
            engine.Stall = null;
            releasePoll.TrySetResult();
        }
    }

    [Fact]
    public async Task A_late_old_load_cannot_override_the_replacement_session()
    {
        CreateTracks("01.mp3", "02.mp3");
        var loader = new FolderTrackCatalogLoader(dir.FullName);
        var catalog = await loader.LoadAsync();

        TrackCatalog Single(LocalTrack track)
        {
            return new TrackCatalog([new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [track])]);
        }

        var oldPlayer = await LocalSource.Create(engineSession, loader, Single(catalog.AllFiles.Tracks[0]),
                                                 disposeTimeout: TimeSpan.FromMilliseconds(100));

        var releaseOldLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNewLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadNumber = 0;
        engine.Stall = op => op != "Load" ? null : ++loadNumber == 1 ? releaseOldLoad.Task : releaseNewLoad.Task;
        LocalSource? replacement = null;

        try
        {
            oldPlayer.Play();
            await WaitForLoads(1);
            await oldPlayer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            replacement = await LocalSource.Create(engineSession, loader, Single(catalog.AllFiles.Tracks[1]));
            var staleEventWasPublished = false;
            replacement.OnPlaybackChanged += view =>
            {
                if (view.Queue.State == PlaybackState.Playing) staleEventWasPublished = true;
            };
            replacement.Play();
            await replacement.DrainForTests();

            releaseOldLoad.TrySetResult();
            await WaitForLoads(2);
            // The old load and following stop have both emitted their events; the new load
            // is dispatched but cannot emit yet. This blocks the risky window.
            await replacement.DrainForTests();
            Assert.Equal(["Load", "Stop", "Load"], engine.Ops.Take(3));
            Assert.False(staleEventWasPublished, "the replacement player must ignore the old session's engine event");

            releaseNewLoad.TrySetResult();
            await TestWait.Assert(
                () => engine.Snapshot is { State: PlaybackState.Playing } snapshot &&
                      snapshot.Path == catalog.AllFiles.Tracks[1].FilePath, "the replacement load lands");
        } finally
        {
            releaseOldLoad.TrySetResult();
            releaseNewLoad.TrySetResult();
            if (replacement is not null) await replacement.DisposeAsync();
        }
    }

    [Fact]
    public async Task Advances_through_the_playlist_and_wraps_around()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();

        player.Play();
        await WaitForLoads(1);

        engine.FinishTrack();
        await WaitForLoads(2);

        engine.FinishTrack(); // last track done: wrap to the beginning
        await WaitForLoads(3);

        Assert.Equal(["01.mp3", "02.mp3", "01.mp3"], LoadedFiles);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task A_terminal_engine_update_advances_the_source()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        player.Play();
        await WaitForLoads(1);
        var playbackId = engine.Snapshot.PlaybackId;

        engine.RaiseUpdated(new EngineSnapshot(PlaybackState.Stopped, null, null, null, DateTimeOffset.UtcNow, playbackId,
                                               engine.Snapshot.Sequence + 1, EndReason.Finished));

        await WaitForLoads(2);
        Assert.Equal(["01.mp3", "02.mp3"], LoadedFiles);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task A_playlist_where_everything_fails_stops_instead_of_spinning()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();

        player.Play();
        await WaitForLoads(1);
        engine.FailTrack();
        await WaitForLoads(2);
        engine.FailTrack(); // every track has now failed: give up, don't loop forever

        await TestWait.Assert(() => player.State == PlaybackState.Stopped, "player gives up");
        player.Volume(0.5f); // engine-queue barrier after any accidental retry
        Assert.True(await engine.WaitForCall("SetVolume"), "engine queue drains");
        Assert.Equal(2, LoadedFiles.Length);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Load_dispatch_failures_use_the_same_bounded_skip_policy()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        engine.Intercept = op => op == "Load" ? new InvalidOperationException("RPC rejected the load") : null;

        player.Play();

        await TestWait.Assert(() => LoadedFiles.Length == 2, "each track is attempted once");
        await player.DrainForTests(); // local-playback mailbox barrier
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(["01.mp3", "02.mp3"], LoadedFiles);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task A_failed_pause_can_be_retried_from_the_observed_playing_state()
    {
        CreateTracks("01.mp3");
        var player = await CreatePlayer();
        player.Play();
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "track starts");
        var pauseCalls = 0;
        engine.Intercept = op => op == "Pause" && Interlocked.Increment(ref pauseCalls) == 1
                                     ? new InvalidOperationException("device rejected pause")
                                     : null;

        player.Pause();
        await TestWait.Assert(() => pauseCalls == 1, "the first pause fails");
        Assert.Equal(PlaybackState.Playing, player.State);

        player.Pause();
        await TestWait.Assert(() => player.State == PlaybackState.Paused, "the retry lands");
        Assert.Equal(2, pauseCalls);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task One_good_track_resets_the_failure_streak()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();

        player.Play();
        await WaitForLoads(1);
        engine.FailTrack(); // 01 fails (streak 1)
        await WaitForLoads(2);
        engine.FinishTrack(); // 02 plays fine (streak resets)
        await WaitForLoads(3);
        engine.FailTrack(); // 01 fails again (streak 1, NOT 2)
        await WaitForLoads(4);

        Assert.DoesNotContain("Stop", engine.Ops);
        Assert.Equal(["01.mp3", "02.mp3", "01.mp3", "02.mp3"], LoadedFiles);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Playing_a_library_track_activates_the_entire_source_at_that_track()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3", "04.mp3");
        var player = await CreatePlayer();

        player.PlayNow(player.Library[2]);
        await WaitForLoads(1);
        Assert.Empty(player.Queue);
        Assert.Equal(["04.mp3", "01.mp3", "02.mp3"], UpNextFiles(player));

        engine.FinishTrack();
        await WaitForLoads(2);
        engine.FinishTrack();
        await WaitForLoads(3);

        Assert.Equal(["03.mp3", "04.mp3", "01.mp3"], LoadedFiles);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Manual_queue_allows_duplicates_and_prefixes_the_virtual_source_order()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3");
        var player = await CreatePlayer();
        player.PlayNow(player.Library[0]);
        await WaitForLoads(1);

        player.AddToEnd(player.Library[2]);
        player.AddNext(player.Library[2]);
        await player.DrainForTests();

        Assert.Equal(2, player.Queue.Count);
        Assert.NotEqual(player.Queue[0].Id, player.Queue[1].Id);
        Assert.Equal(["03.mp3", "03.mp3", "02.mp3", "03.mp3"], UpNextFiles(player));
        Assert.True(player.UpNext[0].IsQueued);
        Assert.True(player.UpNext[1].IsQueued);
        Assert.False(player.UpNext[2].IsQueued);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Manual_entries_are_consumed_then_playback_resumes_the_interrupted_source()
    {
        CreateTracks("01.mp3", "02.mp3", "override.mp3");
        var player = await CreatePlayer();
        player.PlayNow(player.Library[0]);
        await WaitForLoads(1);
        player.AddNext(player.Library[2]);
        await player.DrainForTests();

        engine.FinishTrack();
        await WaitForLoads(2);
        Assert.Empty(player.Queue);
        Assert.Equal(["02.mp3", "override.mp3"], UpNextFiles(player));

        engine.FinishTrack();
        await WaitForLoads(3);
        Assert.Equal(["01.mp3", "override.mp3", "02.mp3"], LoadedFiles);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Queue_edits_use_occurrence_identity_without_touching_the_source()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3");
        var player = await CreatePlayer();
        player.PlayNow(player.Library[0]);
        await WaitForLoads(1);
        player.AddToEnd(player.Library[2]);
        player.AddToEnd(player.Library[2]);
        await player.DrainForTests();
        var first = player.Queue[0].Id;
        var duplicate = player.Queue[1].Id;

        player.Move(duplicate, -1);
        player.Remove(first);
        await player.DrainForTests();

        Assert.Single(player.Queue);
        Assert.Equal(duplicate, player.Queue[0].Id);
        Assert.Equal(["03.mp3", "02.mp3", "03.mp3"], UpNextFiles(player));
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Clearing_the_manual_queue_leaves_source_playback_running()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3");
        var player = await CreatePlayer();
        player.Play();
        await WaitForLoads(1);
        player.AddNext(player.Library[2]);
        await player.DrainForTests();

        player.ClearQueue();
        await player.DrainForTests();
        Assert.Empty(player.Queue);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(["02.mp3", "03.mp3"], UpNextFiles(player));
        engine.FinishTrack();
        await WaitForLoads(2);

        Assert.EndsWith("02.mp3", player.CurrentTrack!.FilePath);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Shuffle_upcoming_keeps_current_and_preserves_queue_and_source_members()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3", "04.mp3", "05.mp3");
        var player = await CreatePlayer();
        player.PlayNow(player.Library[2]);
        await WaitForLoads(1);
        player.AddToEnd(player.Library[0]);
        player.AddToEnd(player.Library[1]);
        await player.DrainForTests();
        var current = player.CurrentTrack;
        var queueEntries = player.Queue.ToArray();
        var sourceMembers = player.UpNext.Where(item => !item.IsQueued)
                                  .Select(item => item.Track.FilePath)
                                  .OrderBy(path => path)
                                  .ToArray();

        player.ShuffleUpcoming();
        await player.DrainForTests();

        Assert.Equal(current, player.CurrentTrack);
        Assert.Equal(queueEntries, player.Queue);
        Assert.Equal(sourceMembers,
                     player.UpNext.Where(item => !item.IsQueued).Select(item => item.Track.FilePath).OrderBy(path => path));
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Next_on_an_empty_playlist_does_not_create_playback_intent()
    {
        var player = await CreatePlayer();
        var second = new LocalTrack(Path.Combine(dir.FullName, "02.mp3"), "02.mp3", "02.mp3");

        player.Next();
        player.AddToEnd(second);
        await player.DrainForTests();
        player.Volume(0.5f); // engine-queue barrier for any stop emitted by replacement
        Assert.True(await engine.WaitForCall("SetVolume"), "engine queue did not drain");

        Assert.DoesNotContain("Stop", engine.Ops);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Null(player.CurrentTrack);
        Assert.Equal(second, Assert.Single(player.Queue).Track);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Reconnect_reloads_the_selected_track_and_preserves_pause_intent()
    {
        CreateTracks("01.mp3");
        var player = await CreatePlayer();
        player.Play();
        await WaitForLoads(1);
        player.Pause();
        await TestWait.Assert(() => player.State == PlaybackState.Paused, "track pauses");

        engine.DisconnectTrack();
        await TestWait.Assert(() => player.State == PlaybackState.Stopped, "disconnect is observed");
        var loadsBeforeReconnect = LoadedFiles.Length;

        engineSession.OnEngineReconnected();

        await WaitForLoads(loadsBeforeReconnect + 1);
        await TestWait.Assert(() => player.State == PlaybackState.Paused, "paused track is restored");
        Assert.EndsWith("01.mp3", player.CurrentTrack!.FilePath);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Intent_changes_while_disconnected_wait_for_reconnect()
    {
        CreateTracks("01.mp3");
        var player = await CreatePlayer();
        player.Play();
        await WaitForLoads(1);

        engine.DisconnectTrack();
        await TestWait.Assert(() => player.State == PlaybackState.Stopped, "disconnect is observed");
        player.Pause();
        await player.DrainForTests();
        Assert.Single(LoadedFiles);

        engineSession.OnEngineReconnected();

        await WaitForLoads(2);
        await TestWait.Assert(() => player.State == PlaybackState.Paused, "latest intent is restored");
        await player.DisposeAsync();
    }

    [Fact]
    public async Task A_delayed_engine_event_is_never_attributed_to_a_newer_track_selection()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadNumber = 0;
        engine.Stall = op => op != "Load" ? null : ++loadNumber == 1 ? releaseFirst.Task : releaseSecond.Task;

        string? eventPath = null;
        string? attributedPath = null;
        player.OnPlaybackChanged += view =>
        {
            var actual = engine.Snapshot.Path;
            var attributed = view.Queue.CurrentTrack?.FilePath;
            if (view.Queue.State == PlaybackState.Playing &&
                actual is not null &&
                !string.Equals(actual, attributed, StringComparison.OrdinalIgnoreCase))
            {
                eventPath ??= actual;
                attributedPath ??= attributed;
            }
        };

        try
        {
            player.PlayNow(player.Library[0]);
            await WaitForLoads(1);
            player.PlayNow(player.Library[1]);
            await TestWait.Assert(() => CurrentLibraryIndex(player) == 1, "the newer selection is accepted");

            releaseFirst.TrySetResult();
            await WaitForLoads(2); // the second load is now dispatched but deliberately stalled

            // The first engine event was posted before this mailbox barrier. A correct player
            // may publish track 01 or ignore that stale event, but must never call it track 02.
            await player.DrainForTests();
        } finally
        {
            releaseSecond.TrySetResult();
            await player.DisposeAsync();
        }

        Assert.True(eventPath is null, $"engine event for '{eventPath}' was attributed to '{attributedPath}'");
    }

    [Fact]
    public async Task A_stale_end_event_cannot_advance_past_a_newer_selection()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3");
        var player = await CreatePlayer();
        player.PlayNow(player.Library[0]);
        await WaitForLoads(1);

        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stall = op => op == "Load" ? releaseSecond.Task : null;

        try
        {
            player.PlayNow(player.Library[1]);
            await WaitForLoads(2);
            await TestWait.Assert(() => UpNextFiles(player).FirstOrDefault() == "03.mp3",
                                  "the newer source cursor is accepted");

            engine.FinishTrack();         // physical track 01 ends while load 02 is still queued
            await player.DrainForTests(); // mailbox barrier for the end event

            Assert.Equal("03.mp3", UpNextFiles(player).First());
            Assert.Equal(2, LoadedFiles.Length); // no spurious load of track 03
        } finally
        {
            releaseSecond.TrySetResult();
            await player.DisposeAsync();
        }
    }

    // ---- prev -------------------------------------------------------------------

    [Fact]
    public async Task Prev_early_in_a_track_goes_to_the_previous_track()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        player.PlayNow(player.Library[1]);
        await WaitForLoads(1);
        await TestWait.Assert(() => player.Position is not null, "load committed position 0");

        player.Prev(); // position 0s <= 5s: go back
        await WaitForLoads(2);
        Assert.EndsWith("01.mp3", player.CurrentTrack!.FilePath);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Prev_late_in_a_track_replays_it()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        player.PlayNow(player.Library[1]);
        await WaitForLoads(1);

        player.Seek(TimeSpan.FromSeconds(10)); // commits position via the engine event
        await TestWait.Assert(() => player.Position?.Current == TimeSpan.FromSeconds(10), "seek lands");

        player.Prev(); // >5s in: restart the SAME track
        await WaitForLoads(2);
        Assert.EndsWith("02.mp3", player.CurrentTrack!.FilePath);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Prev_at_the_first_track_replays_it()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        player.Play(); // index 0
        await WaitForLoads(1);
        await TestWait.Assert(() => player.Position is not null, "load committed position 0");

        player.Prev(); // Index-- would go to -1: clamps back to 0
        await WaitForLoads(2);
        Assert.EndsWith("01.mp3", player.CurrentTrack!.FilePath);
        Assert.Equal(0, CurrentLibraryIndex(player));
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Next_then_immediate_previous_returns_to_the_original_track_while_load_is_in_flight()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        player.Play();
        await WaitForLoads(1);

        // Make the old position deliberately "late": without clearing it when Next is
        // selected, Prev would interpret this as a request to replay 02 instead of go
        // back to 01.
        player.Seek(TimeSpan.FromSeconds(10));
        await TestWait.Assert(() => player.Position?.Current == TimeSpan.FromSeconds(10), "seek lands");

        var releaseLoads = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stall = op => op == "Load" ? releaseLoads.Task : null;
        player.Next();
        await WaitForLoads(2);

        player.Prev();
        releaseLoads.TrySetResult();

        await WaitForLoads(3);
        await TestWait.Assert(() => engine.Snapshot.Path == player.Library[0].FilePath, "previous returns to 01");
        Assert.Equal(["01.mp3", "02.mp3", "01.mp3"], LoadedFiles);
        Assert.Equal(0, CurrentLibraryIndex(player));
        await player.DisposeAsync();
    }

    // ---- scanning ------------------------------------------------------------------

    [Fact]
    public async Task Scan_recurses_and_matches_every_supported_extension_once()
    {
        CreateTracks("a.mp3", "b.opus", "c.m4a");
        TestData.CreateTrack(dir, "notes.txt"); // not audio: excluded
        var sub = Directory.CreateDirectory(Path.Combine(dir.FullName, "deep"));
        TestData.CreateTrack(sub, "nested.flac");

        var player = await CreatePlayer();

        string[] names = [.. player.Library.Select(t => Path.GetFileName(t.FilePath))];
        Assert.Equal(4, names.Length);
        Assert.Contains("nested.flac", names);     // recursion
        Assert.DoesNotContain("notes.txt", names); // filter
        Assert.Single(names, n => n == "c.m4a");   // no two extension patterns may match one file
        await player.DisposeAsync();
    }

    // ---- update loop -----------------------------------------------------------------

    [Fact]
    public async Task The_position_poll_survives_errors_and_keeps_polling()
    {
        CreateTracks("01.mp3");
        var player = await CreateFastPlayer();
        player.Play();
        await WaitForLoads(1);

        // Only the poll can see this: no engine event fires for a duration change.
        engine.TrackDuration = TimeSpan.FromMinutes(5);
        await TestWait.Assert(() => player.Position is { Total.TotalMinutes: 5 }, "poll picks up the change");

        engine.Intercept = op => op == "GetState" ? new InvalidOperationException("rpc blip") : null;
        await Task.Delay(150); // loop hits the error and backs off
        engine.Intercept = null;

        engine.TrackDuration = TimeSpan.FromMinutes(7);
        await TestWait.Assert(() => player.Position is { Total.TotalMinutes: 7 }, "poll recovered after the blip");
        await player.DisposeAsync();
    }

    // ---- direct transport ----------------------------------------------------------

    [Fact]
    public async Task Next_and_stop_work_through_the_public_surface()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        player.Play();
        await WaitForLoads(1);

        player.Next();
        await WaitForLoads(2);
        Assert.Equal(["01.mp3", "02.mp3"], LoadedFiles);

        player.Stop();
        await TestWait.Assert(() => engine.Snapshot.State == PlaybackState.Stopped, "stop lands");
        await player.DisposeAsync();
    }
}
