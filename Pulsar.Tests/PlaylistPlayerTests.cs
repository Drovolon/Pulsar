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

public class PlaylistTests : IAsyncLifetime
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-dp-test-");
    private readonly FakeRemoteEngine engine = new();
    private readonly EngineSession engineSession;

    public PlaylistTests() => engineSession = new EngineSession(
        engine,
        pollPlaying: TimeSpan.FromMilliseconds(25),
        pollIdle: TimeSpan.FromMilliseconds(25),
        errorBackoff: TimeSpan.FromMilliseconds(50),
        disposeTimeout: TimeSpan.FromMilliseconds(200));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await engineSession.DisposeAsync();
        dir.Delete(recursive: true);
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
        return await LocalSource.Create(
            engineSession, loader, catalog, disposeTimeout: TimeSpan.FromMilliseconds(200));
    }

    private Task<LocalSource> CreateFastPlayer() => CreatePlayer();

    private string[] LoadedFiles => [.. engine.Calls
        .Where(c => c.Op == "Load")
        .Select(c => Path.GetFileName((((string, TimeSpan, bool))c.Arg!).Item1))];

    private async Task WaitForLoads(int count)
        => await TestWait.Assert(() => LoadedFiles.Length >= count, $"{count} loads dispatched");

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
        }
        finally
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
        }
        finally
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
        TrackCatalog Single(LocalTrack track) => new(
            [new TrackGroup(TrackCatalog.AllFilesId, TrackCatalog.AllFilesName, [track])]);
        var oldPlayer = await LocalSource.Create(
            engineSession, loader, Single(catalog.AllFiles.Tracks[0]),
            disposeTimeout: TimeSpan.FromMilliseconds(100));

        var releaseOldLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNewLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadNumber = 0;
        engine.Stall = op => op != "Load"
            ? null
            : ++loadNumber == 1 ? releaseOldLoad.Task : releaseNewLoad.Task;
        LocalSource? replacement = null;

        try
        {
            oldPlayer.Play();
            await WaitForLoads(1);
            await oldPlayer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

            replacement = await LocalSource.Create(
                engineSession, loader, Single(catalog.AllFiles.Tracks[1]));
            var staleEventWasPublished = false;
            replacement.OnPlaybackChanged += view =>
            {
                if (view.State == PlaybackState.Playing) staleEventWasPublished = true;
            };
            replacement.Play();
            await replacement.ReplacePlaylistAsync(replacement.Tracks);

            releaseOldLoad.TrySetResult();
            await WaitForLoads(2);
            // The old load and following stop have both emitted their events; the new load
            // is dispatched but cannot emit yet. This is a barrier on the exact risky window.
            await replacement.ReplacePlaylistAsync(replacement.Tracks);
            Assert.Equal(["Load", "Stop", "Load"], engine.Ops.Take(3));
            Assert.False(staleEventWasPublished,
                "the replacement player must ignore the old session's engine event");

            releaseNewLoad.TrySetResult();
            await TestWait.Assert(
                () => engine.Snapshot is { State: PlaybackState.Playing } snapshot
                      && snapshot.Path == catalog.AllFiles.Tracks[1].FilePath,
                "the replacement load lands");
        }
        finally
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
        engine.Intercept = op => op == "Load"
            ? new InvalidOperationException("RPC rejected the load")
            : null;

        player.Play();

        await TestWait.Assert(() => LoadedFiles.Length == 2, "each track is attempted once");
        await player.ReplacePlaylistAsync(player.Tracks); // local-playback mailbox barrier
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
        engine.FailTrack();          // 01 fails (streak 1)
        await WaitForLoads(2);
        engine.FinishTrack();        // 02 plays fine (streak resets)
        await WaitForLoads(3);
        engine.FailTrack();          // 01 fails again (streak 1, NOT 2)
        await WaitForLoads(4);

        Assert.DoesNotContain("Stop", engine.Ops);
        Assert.Equal(["01.mp3", "02.mp3", "01.mp3", "02.mp3"], LoadedFiles);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Toggling_shuffle_keeps_the_current_track_and_the_library()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3", "04.mp3", "05.mp3");
        var player = await CreatePlayer();

        player.PlayIndex(2);
        await WaitForLoads(1);
        var current = player.CurrentTrack;

        player.SetShuffle(true);
        await TestWait.Assert(() => player.Shuffle, "shuffle turns on");

        Assert.Equal(current, player.CurrentTrack); // shuffling mustn't change what's playing
        Assert.Equal(
            new DirectoryInfo(dir.FullName).GetFiles("*.mp3").Select(f => f.FullName).OrderBy(x => x),
            player.Tracks.Select(t => t.FilePath).OrderBy(x => x)); // same library, different order

        player.SetShuffle(false);
        await TestWait.Assert(() => !player.Shuffle, "shuffle turns off");
        Assert.Equal(current, player.CurrentTrack);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Shuffle_selected_while_empty_applies_when_tracks_are_added()
    {
        var player = await CreatePlayer();

        player.SetShuffle(true);
        CreateTracks("01.mp3", "02.mp3");
        var catalog = await new FolderTrackCatalogLoader(dir.FullName).LoadAsync();
        await player.ReplacePlaylistAsync(catalog.AllFiles.Tracks);

        Assert.True(player.Shuffle);
        Assert.Equal(2, player.Tracks.Count);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Next_on_an_empty_playlist_does_not_create_playback_intent()
    {
        var player = await CreatePlayer();
        var first = new LocalTrack(Path.Combine(dir.FullName, "01.mp3"), "01.mp3", "01.mp3");
        var second = new LocalTrack(Path.Combine(dir.FullName, "02.mp3"), "02.mp3", "02.mp3");

        player.Next();
        await player.ReplacePlaylistAsync([first]);
        await player.ReplacePlaylistAsync([second]);
        player.Volume(0.5f); // engine-queue barrier for any stop emitted by replacement
        Assert.True(await engine.WaitForCall("SetVolume"), "engine queue did not drain");

        Assert.DoesNotContain("Stop", engine.Ops);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(second, player.CurrentTrack);
        await player.DisposeAsync();
    }

    // ---- rescan ---------------------------------------------------------------

    [Fact]
    public async Task Rescan_follows_the_current_track_to_its_new_index()
    {
        CreateTracks("01.mp3", "03.mp3");
        var player = await CreatePlayer();
        player.PlayIndex(1); // 03
        await WaitForLoads(1);

        TestData.CreateTrack(dir, "02.mp3"); // lands between them
        var rescanned = await new FolderTrackCatalogLoader(dir.FullName).LoadAsync();
        await player.ReplacePlaylistAsync(rescanned.AllFiles.Tracks);

        Assert.Equal(3, player.Tracks.Count);
        Assert.Equal(2, player.Index); // 03 moved from index 1 to 2
        Assert.EndsWith("03.mp3", player.CurrentTrack!.FilePath);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Rescan_resets_to_the_start_when_the_current_track_vanished()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        player.PlayIndex(1); // 02
        await WaitForLoads(1);

        File.Delete(Path.Combine(dir.FullName, "02.mp3"));
        var rescanned = await new FolderTrackCatalogLoader(dir.FullName).LoadAsync();
        await player.ReplacePlaylistAsync(rescanned.AllFiles.Tracks);

        Assert.Equal(0, player.Index);
        await TestWait.Assert(() => player.State == PlaybackState.Stopped, "removed track stops");
        Assert.EndsWith("01.mp3", player.CurrentTrack!.FilePath);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Rescan_preserves_shuffle_and_includes_new_files()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3");
        var player = await CreatePlayer();
        player.SetShuffle(true);
        await TestWait.Assert(() => player.Shuffle, "shuffle turns on");

        TestData.CreateTrack(dir, "04.mp3");
        var rescanned = await new FolderTrackCatalogLoader(dir.FullName).LoadAsync();
        await player.ReplacePlaylistAsync(rescanned.AllFiles.Tracks);

        Assert.True(player.Shuffle, "rescan must not silently unshuffle");
        Assert.Equal(4, player.Tracks.Count);
        Assert.Contains(player.Tracks, t => t.FilePath.EndsWith("04.mp3"));
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Replacing_playlist_preserves_a_shared_playing_track_and_updates_next()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3");
        var player = await CreatePlayer();
        player.PlayIndex(1);
        await WaitForLoads(1);
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "02 starts playing");
        var replacement = player.Tracks.Where(track => !track.FilePath.EndsWith("01.mp3")).ToArray();
        var changes = 0;
        player.OnQueueChanged += () => changes++;

        await player.ReplacePlaylistAsync(replacement);

        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.EndsWith("02.mp3", player.CurrentTrack!.FilePath);
        Assert.EndsWith("03.mp3", player.NextTrack!.FilePath);
        Assert.DoesNotContain("Stop", engine.Ops);
        Assert.True(changes > 0, "queue-only changes must notify the source for next-track prefetch");
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Replacing_playlist_refreshes_metadata_for_the_shared_playing_track()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        player.Play();
        await WaitForLoads(1);
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "01 starts playing");
        var replacement = player.Tracks
            .Select(track => track with { DisplayName = $"Group: {track.DisplayName}" })
            .ToArray();

        await player.ReplacePlaylistAsync(replacement);

        Assert.Equal("Group: 01.mp3", player.CurrentTrack!.DisplayName);
        Assert.Equal("Group: 02.mp3", player.NextTrack!.DisplayName);
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
        await player.ReplacePlaylistAsync(player.Tracks); // player-mailbox barrier
        Assert.Single(LoadedFiles);

        engineSession.OnEngineReconnected();

        await WaitForLoads(2);
        await TestWait.Assert(() => player.State == PlaybackState.Paused, "latest intent is restored");
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Replacing_playlist_stops_when_the_playing_track_is_absent()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        player.PlayIndex(1);
        await WaitForLoads(1);
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "02 starts playing");

        await player.ReplacePlaylistAsync([player.Tracks[0]]);

        await TestWait.Assert(() => player.State == PlaybackState.Stopped, "removed track stops");
        Assert.Equal(0, player.Index);
        Assert.EndsWith("01.mp3", player.CurrentTrack!.FilePath);
        await TestWait.Assert(() => engine.Ops.Contains("Stop"), "removed current track stops the host");
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Replacing_playlist_stops_a_removed_track_whose_load_is_still_in_flight()
    {
        CreateTracks("01.mp3", "02.mp3");
        var player = await CreatePlayer();
        var replacement = new[] { player.Tracks[0] };
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stall = op => op == "Load" ? releaseLoad.Task : null;
        var relabeledAsPlaying = false;
        player.OnPlaybackChanged += view =>
        {
            if (view.State == PlaybackState.Playing) relabeledAsPlaying = true;
        };

        player.PlayIndex(1);
        await WaitForLoads(1);
        await player.ReplacePlaylistAsync(replacement);

        releaseLoad.TrySetResult();
        await TestWait.Assert(() => engine.Ops.Contains("Stop"), "the queued load is followed by stop");
        await TestWait.Assert(() => engine.Snapshot.State == PlaybackState.Stopped, "the host stops");
        // A completed replacement is also a mailbox barrier for the preceding engine events.
        await player.ReplacePlaylistAsync(player.Tracks);

        Assert.False(relabeledAsPlaying, "the removed file must not be published as the replacement track");
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.EndsWith("01.mp3", player.CurrentTrack!.FilePath);
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
        engine.Stall = op => op != "Load"
            ? null
            : ++loadNumber == 1 ? releaseFirst.Task : releaseSecond.Task;

        string? eventPath = null;
        string? attributedPath = null;
        player.OnPlaybackChanged += view =>
        {
            var actual = engine.Snapshot.Path;
            var attributed = view.CurrentTrack?.FilePath;
            if (view.State == PlaybackState.Playing
                && actual is not null
                && !string.Equals(actual, attributed, StringComparison.OrdinalIgnoreCase))
            {
                eventPath ??= actual;
                attributedPath ??= attributed;
            }
        };

        try
        {
            player.PlayIndex(0);
            await WaitForLoads(1);
            player.PlayIndex(1);
            await TestWait.Assert(() => player.Index == 1, "the newer selection is accepted");

            releaseFirst.TrySetResult();
            await WaitForLoads(2); // the second load is now dispatched but deliberately stalled

            // The first engine event was posted before this mailbox barrier. A correct player
            // may publish track 01 or ignore that stale event, but must never call it track 02.
            await player.ReplacePlaylistAsync(player.Tracks);
        }
        finally
        {
            releaseSecond.TrySetResult();
            await player.DisposeAsync();
        }

        Assert.True(eventPath is null,
            $"engine event for '{eventPath}' was attributed to '{attributedPath}'");
    }

    [Fact]
    public async Task A_stale_end_event_cannot_advance_past_a_newer_selection()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3");
        var player = await CreatePlayer();
        player.PlayIndex(0);
        await WaitForLoads(1);

        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stall = op => op == "Load" ? releaseSecond.Task : null;

        try
        {
            player.PlayIndex(1);
            await WaitForLoads(2);
            await TestWait.Assert(() => player.Index == 1, "the newer selection is accepted");

            engine.FinishTrack(); // physical track 01 ends while load 02 is still queued
            await player.ReplacePlaylistAsync(player.Tracks); // mailbox barrier for the end event

            Assert.Equal(1, player.Index);
            Assert.Equal(2, LoadedFiles.Length); // no spurious load of track 03
        }
        finally
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
        player.PlayIndex(1);
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
        player.PlayIndex(1);
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
        Assert.Equal(0, player.Index);
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
        await TestWait.Assert(() => engine.Snapshot.Path == player.Tracks[0].FilePath, "previous returns to 01");
        Assert.Equal(["01.mp3", "02.mp3", "01.mp3"], LoadedFiles);
        Assert.Equal(0, player.Index);
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

        string[] names = [.. player.Tracks.Select(t => Path.GetFileName(t.FilePath))];
        Assert.Equal(4, names.Length);
        Assert.Contains("nested.flac", names);            // recursion
        Assert.DoesNotContain("notes.txt", names);        // filter
        Assert.Single(names, n => n == "c.m4a");          // no two extension patterns may match one file
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
        await TestWait.Assert(() => engine.Snapshot.State == NAudio.Wave.PlaybackState.Stopped, "stop lands");
        await player.DisposeAsync();
    }
}
