using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Broadcast.Beefweb;
using Pulsar.Tests.Fakes;
using Xunit;
using PulsarState = NAudio.Wave.PlaybackState;

namespace Pulsar.Tests;

/// <summary>
/// End-to-end scenarios for the beefweb Watcher: observations go in via a scripted feed;
/// out come Current snapshots, OnChanged wakeups, status, and chat warnings. Next-track
/// prefetch and player commands run through a real Beefweb.Client against the in-process
/// server. Only syncable, actually-playing sources produce snapshots, and OnChanged
/// fires only for real cursor events.
/// </summary>
public class BeefwebWatcherTests : IAsyncLifetime
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-watcher-test-");
    private readonly FakeBeefwebServer server = new();
    private readonly ControllableFeed feed = new();
    private Watcher? watcher;
    private int changes;

    public BeefwebWatcherTests() => TestBootstrap.Chat.Clear();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (watcher is not null) await watcher.DisposeAsync();
        dir.Delete(recursive: true);
    }

    private Watcher Create(TimeSpan? giveUpDelay = null)
    {
        // The watcher takes ownership of the client (disposes it); the server outlives it.
        watcher = new Watcher(feed, server.CreateClient(), isWine: false, giveUpDelay);
        watcher.OnChanged += () => Interlocked.Increment(ref changes);
        return watcher;
    }

    private int Changes => Volatile.Read(ref changes);

    // OnChanged fires only after next-track resolution, later than Current flips non-null;
    // waiting on the wakeup is the only race-free way to assert on counts or NextFilePath.
    private Task AwaitWake(int atLeast, string because)
        => TestWait.Assert(() => Changes >= atLeast, because);

    private string CreateTrack(string name) => TestData.CreateTrack(dir, name);

    private static Observation Obs(string? path, PulsarState state = PulsarState.Playing,
                                   double positionSeconds = 5, double durationSeconds = 60,
                                   string title = "", string artist = "", long? monoStamp = null)
        => new(path, state, TimeSpan.FromSeconds(positionSeconds), TimeSpan.FromSeconds(durationSeconds),
               title, artist, DateTimeOffset.UtcNow, monoStamp ?? Stopwatch.GetTimestamp());

    [Fact]
    public async Task A_playing_local_track_becomes_a_snapshot()
    {
        var w = Create();
        var track = CreateTrack("song.flac");

        feed.Push(Obs(track, positionSeconds: 12, durationSeconds: 240, title: "Song", artist: "Band"));

        await TestWait.Assert(() => w.Current is not null, "snapshot appears");
        var s = w.Current!;
        Assert.Equal(track, s.FilePath);
        Assert.True(s.IsPlaying);
        Assert.Equal(TimeSpan.FromSeconds(12), s.Position);
        Assert.Equal("Song", s.Meta.Title);
        Assert.Equal("Band", s.Meta.Artist);
        Assert.Equal(240_000, s.Meta.DurationMs);
        Assert.Equal("song.flac", s.Meta.OriginalFileName);

        Assert.True(w.Status.Connected);
        Assert.Null(w.Status.Unsyncable);
        await AwaitWake(1, "the track change wakes the pipeline");
    }

    [Fact]
    public async Task Boring_updates_do_not_wake_the_pipeline()
    {
        var w = Create();
        var track = CreateTrack("song.flac");
        var t0 = Stopwatch.GetTimestamp();

        feed.Push(Obs(track, positionSeconds: 5, monoStamp: t0));
        await AwaitWake(1, "initial track change");
        var seen = Changes;

        // A cursor advancing in lockstep with time is what any volume/UI event looks
        // like; forwarding it would spam IPC every second.
        feed.Push(Obs(track, positionSeconds: 6, monoStamp: t0 + Stopwatch.Frequency));
        // Sentinel: a real event that must land AFTER the boring one.
        feed.Push(Obs(track, state: PulsarState.Paused, positionSeconds: 6,
                      monoStamp: t0 + Stopwatch.Frequency));

        await TestWait.Assert(() => Changes > seen, "the pause came through");
        Assert.Equal(seen + 1, Changes); // pause fired; the boring update didn't
    }

    [Fact]
    public async Task Pausing_updates_the_snapshot()
    {
        var w = Create();
        var track = CreateTrack("song.flac");
        feed.Push(Obs(track));
        await TestWait.Assert(() => w.Current is { IsPlaying: true }, "playing snapshot");

        feed.Push(Obs(track, state: PulsarState.Paused, positionSeconds: 9));

        await TestWait.Assert(() => w.Current is { IsPlaying: false }, "paused snapshot");
        Assert.Equal(track, w.Current!.FilePath); // still the same broadcastable file
    }

    [Fact]
    public async Task Stopping_the_player_clears_the_snapshot()
    {
        var w = Create();
        var track = CreateTrack("song.flac");
        feed.Push(Obs(track));
        await TestWait.Assert(() => w.Current is not null, "playing snapshot");

        feed.Push(Obs(null, state: PulsarState.Stopped, positionSeconds: 0));

        await TestWait.Assert(() => w.Current is null, "stop clears the snapshot");
        Assert.True(w.Status.Connected, "stopped is a healthy state, not a connection problem");
    }

    [Fact]
    public async Task Web_radio_is_unsyncable_and_warned_about_once()
    {
        var w = Create();
        feed.Push(Obs("https://radio.example/stream", title: "ChillFM"));

        await TestWait.Assert(() => w.Status.Unsyncable is not null, "unsyncable status");
        Assert.Null(w.Current); // never broadcast something listeners can't fetch
        Assert.Equal(UnsyncableReason.InternetRadio, w.Status.Unsyncable!.Reason);
        Assert.Equal("ChillFM", w.Status.Unsyncable.Track);

        await TestWait.Assert(() => TestBootstrap.Chat.Messages.Length == 1, "DJ warned in chat");
        Assert.Contains("internet radio stream", TestBootstrap.Chat.Messages[0]);

        // The player re-reports the stream every second; the DJ must be nagged only once.
        feed.Push(Obs("https://radio.example/stream", positionSeconds: 6, title: "ChillFM"));
        var track = CreateTrack("song.flac");
        feed.Push(Obs(track)); // sentinel proves the repeat was processed
        await TestWait.Assert(() => w.Current is not null, "back to a syncable track");
        Assert.Single(TestBootstrap.Chat.Messages);
        Assert.Null(w.Status.Unsyncable);

        // ...but a fresh transition into unsyncable territory warns again.
        feed.Push(Obs("https://radio.example/other", title: "JazzFM"));
        await TestWait.Assert(() => TestBootstrap.Chat.Messages.Length == 2, "second warning");
    }

    [Fact]
    public async Task The_label_for_an_unsyncable_track_prefers_artist_and_title()
    {
        var w = Create();
        feed.Push(Obs("https://radio.example/stream", title: "Song", artist: "Band"));

        await TestWait.Assert(() => w.Status.Unsyncable is not null, "unsyncable status");
        Assert.Equal("Band - Song", w.Status.Unsyncable!.Track);
    }

    [Fact]
    public async Task The_play_queue_head_is_prefetched_as_the_next_track()
    {
        var w = Create();
        var current = CreateTrack("current.flac");
        var queued = CreateTrack("queued.flac");
        server.SetPlayQueue(queued);

        feed.Push(Obs(current));

        // "What plays next" resolves before the announce, so it can be transcoded ahead.
        await AwaitWake(1, "track change announced");
        Assert.Equal(queued, w.Current!.NextFilePath);
    }

    [Fact]
    public async Task The_next_playlist_item_is_prefetched_when_playback_is_linear()
    {
        var w = Create();
        var current = CreateTrack("current.flac");
        var next = CreateTrack("next.flac");
        server.SetActivePlaylist("p1", activeIndex: 3, (4, next));
        server.SetOptions(("playbackOrder", ["Default", "Random", "Shuffle (tracks)"], 0));

        feed.Push(Obs(current));

        await AwaitWake(1, "track change announced");
        Assert.Equal(next, w.Current!.NextFilePath);
    }

    [Theory]
    [InlineData(1)] // Random
    [InlineData(2)] // Shuffle (tracks)
    public async Task Shuffled_playback_disables_playlist_prefetch(int orderValue)
    {
        // Under shuffle the playlist successor is NOT what plays next: don't prefetch it.
        var w = Create();
        var current = CreateTrack("current.flac");
        var next = CreateTrack("next.flac");
        server.SetActivePlaylist("p1", activeIndex: 3, (4, next));
        server.SetOptions(("playbackOrder", ["Default", "Random", "Shuffle (tracks)"], orderValue));

        feed.Push(Obs(current));

        await AwaitWake(1, "track change announced");
        Assert.Null(w.Current!.NextFilePath);
    }

    [Fact]
    public async Task Unknown_playback_order_disables_playlist_prefetch()
    {
        // If we can't confirm the order is linear, guessing is worse than skipping.
        var w = Create();
        var current = CreateTrack("current.flac");
        var next = CreateTrack("next.flac");
        server.SetActivePlaylist("p1", activeIndex: 3, (4, next));
        // no options reported at all

        feed.Push(Obs(current));

        await AwaitWake(1, "track change announced");
        Assert.Null(w.Current!.NextFilePath);
    }

    [Fact]
    public async Task A_brief_disconnection_holds_the_broadcast()
    {
        var w = Create(); // production 10s give-up
        var track = CreateTrack("song.flac");
        feed.Push(Obs(track));
        await AwaitWake(1, "initial track change");
        var seen = Changes;

        // beefweb restarting (or a hiccup) must not stop the broadcast for everyone.
        feed.SetConnected(false);
        await Task.Delay(200);

        Assert.NotNull(w.Current);
        Assert.True(w.Status.Connected, "status stays green during the grace period");
        Assert.Equal(seen, Changes);
    }

    [Fact]
    public async Task A_sustained_disconnection_gives_up_and_stops()
    {
        var w = Create(giveUpDelay: TimeSpan.FromMilliseconds(200));
        var track = CreateTrack("song.flac");
        feed.Push(Obs(track));
        await AwaitWake(1, "initial track change");
        var seen = Changes;

        feed.SetConnected(false);

        // A player gone for good must release listeners, not freeze them on the last cursor.
        await TestWait.Assert(() => w.Current is null, "give-up clears the snapshot");
        Assert.False(w.Status.Connected);
        Assert.True(Changes > seen, "give-up wakes the pipeline so upstream announces the stop");
    }

    [Fact]
    public async Task Reconnecting_within_the_grace_period_cancels_the_give_up()
    {
        // Wide margins on purpose: a fired give-up only resets on a new observation, so
        // the reconnect must beat the deadline comfortably even on a loaded CI box.
        var giveUp = TimeSpan.FromSeconds(2);
        var w = Create(giveUpDelay: giveUp);
        var track = CreateTrack("song.flac");
        feed.Push(Obs(track));
        await AwaitWake(1, "initial track change");
        var seen = Changes;

        feed.SetConnected(false);
        await Task.Delay(50);
        feed.SetConnected(true);

        await Task.Delay(giveUp + TimeSpan.FromMilliseconds(300)); // past the canceled deadline
        Assert.NotNull(w.Current);
        Assert.True(w.Status.Connected);
        Assert.Equal(seen, Changes); // nobody was woken up for a non-event
    }

    [Fact]
    public async Task Player_commands_hit_the_right_endpoints()
    {
        var w = Create();

        w.TogglePlay();
        w.Next();
        w.Previous();
        w.StopPlayback();

        await TestWait.Assert(() => server.Posts.Length == 4, "all four commands sent");
        // Fire-and-forget commands may interleave; the set is what matters.
        Assert.Equal(
            ["/api/player/next", "/api/player/play-pause", "/api/player/previous", "/api/player/stop"],
            server.Posts.OrderBy(p => p));
    }

    [Fact]
    public async Task A_failed_command_does_not_take_down_the_watcher()
    {
        var w = Create();
        server.Down = true;

        w.Next(); // must not throw, must not kill the pump

        server.Down = false;
        var track = CreateTrack("song.flac");
        feed.Push(Obs(track));
        await TestWait.Assert(() => w.Current is not null, "watcher still processes observations");
    }

    [Fact]
    public async Task Dispose_completes_promptly_while_the_feed_is_live()
    {
        var w = Create();
        feed.Push(Obs(CreateTrack("song.flac")));
        await TestWait.Assert(() => w.Current is not null, "watcher is mid-flight");

        await TestWait.Within(w.DisposeAsync().AsTask(),
                              "dispose finishes successfully despite the endless feed");
        watcher = null;
    }
}
