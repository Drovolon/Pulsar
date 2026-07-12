using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Pulsar.Playback;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

public class DirectoryPlayerTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-dp-test-");
    private readonly FakeRemoteEngine engine = new();

    public void Dispose() => dir.Delete(recursive: true);

    private void CreateTracks(params string[] names)
    {
        foreach (var name in names)
            TestData.CreateTrack(dir, name);
    }

    private async Task<DirectoryPlayer> CreatePlayer()
    {
        var player = new DirectoryPlayer(engine, dir.FullName);
        await player.Initialize();
        return player;
    }

    private async Task<DirectoryPlayer> CreateFastPlayer()
    {
        var player = new DirectoryPlayer(engine, dir.FullName,
            pollPlaying: TimeSpan.FromMilliseconds(25), pollIdle: TimeSpan.FromMilliseconds(25),
            errorBackoff: TimeSpan.FromMilliseconds(50), disposeTimeout: TimeSpan.FromMilliseconds(200));
        await player.Initialize();
        return player;
    }

    private string[] LoadedFiles => [.. engine.Calls
        .Where(c => c.Op == "Load")
        .Select(c => Path.GetFileName((((string, TimeSpan, bool))c.Arg!).Item1))];

    private async Task WaitForLoads(int count)
        => await TestWait.Assert(() => LoadedFiles.Length >= count, $"{count} loads dispatched");

    // Regression test: nothing but dispose ever issues a stop for a replaced
    // DirectoryPlayer - the audio host kept playing after a source switch.
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
        var player = new DirectoryPlayer(engine, dir.FullName,
            pollPlaying: TimeSpan.FromMilliseconds(25), pollIdle: TimeSpan.FromMilliseconds(25),
            errorBackoff: TimeSpan.FromMilliseconds(50), disposeTimeout: TimeSpan.FromMilliseconds(200));
        await player.Initialize();
        player.Play();
        await WaitForLoads(1);

        engine.Stall = op => op == "Stop" ? new TaskCompletionSource().Task : null;

        await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
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

        await TestWait.Assert(() => engine.Ops.Contains("Stop"), "player gives up");
        Assert.Equal(2, LoadedFiles.Length);
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
        var current = player.CurrentTrack;

        player.ToggleShuffle();

        Assert.True(player.Shuffle);
        Assert.Equal(current, player.CurrentTrack); // shuffling mustn't change what's playing
        Assert.Equal(
            new DirectoryInfo(dir.FullName).GetFiles("*.mp3").Select(f => f.FullName).OrderBy(x => x),
            player.Tracks.OrderBy(x => x)); // same library, different order

        player.ToggleShuffle();
        Assert.False(player.Shuffle);
        Assert.Equal(current, player.CurrentTrack);
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
        await player.Rescan();

        Assert.Equal(3, player.Tracks.Count);
        Assert.Equal(2, player.Index); // 03 moved from index 1 to 2
        Assert.EndsWith("03.mp3", player.CurrentTrack!);
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
        await player.Rescan();

        Assert.Equal(0, player.Index);
        Assert.EndsWith("01.mp3", player.CurrentTrack!);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Rescan_preserves_shuffle_and_includes_new_files()
    {
        CreateTracks("01.mp3", "02.mp3", "03.mp3");
        var player = await CreatePlayer();
        player.ToggleShuffle();
        Assert.True(player.Shuffle);

        TestData.CreateTrack(dir, "04.mp3");
        await player.Rescan();

        Assert.True(player.Shuffle, "rescan must not silently unshuffle");
        Assert.Equal(4, player.Tracks.Count);
        Assert.Contains(player.Tracks, t => t.EndsWith("04.mp3"));
        await player.DisposeAsync();
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
        Assert.EndsWith("01.mp3", player.CurrentTrack!);
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
        Assert.EndsWith("02.mp3", player.CurrentTrack!);
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
        Assert.EndsWith("01.mp3", player.CurrentTrack!);
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

        string[] names = [.. player.Tracks.Select(Path.GetFileName)!];
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
