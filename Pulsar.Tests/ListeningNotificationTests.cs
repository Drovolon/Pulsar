using System;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Pulsar.Listening;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

[Collection(ChatNotificationCollection.Name)]
public sealed class ListeningNotificationTests : IAsyncLifetime
{
    private const string AliceTrack = @"C:\sync\alice.opus";
    private const string NextTrack = @"C:\sync\next.opus";

    private readonly FakeRemoteEngine engine = new();
    private readonly Configuration config = TestData.QuietConfiguration();
    private ListeningManager? manager;

    public ListeningNotificationTests() => TestBootstrap.Chat.Clear();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (manager is not null) await manager.DisposeAsync();
        TestBootstrap.Chat.Clear();
    }

    private ListeningManager Create()
        => manager = new ListeningManager(engine, config);

    private static PairData Track(string path, bool playing = true, int epoch = 1,
                                  string title = "Song")
        => new(path, TimeSpan.FromSeconds(60), playing, DateTimeOffset.UtcNow, epoch,
               new TrackMeta { Artist = "Artist", Title = title });

    [Fact]
    public void Notification_defaults_match_the_intended_noise_level()
    {
        var defaults = new Configuration();

        Assert.True(defaults.NotifyNearbyBroadcaster);
        Assert.True(defaults.NotifyNearbyBroadcasterAutoPlayOff);
        Assert.False(defaults.NotifyNearbyBroadcasterWhileBroadcasting);
        Assert.True(defaults.NotifyMutedPlayback);
        Assert.False(defaults.NotifyListeningTrackChanged);
        Assert.True(defaults.NotifyUnsyncableBroadcast);
    }

    [Fact]
    public async Task Auto_play_off_replaces_the_generic_nearby_notification()
    {
        config.ListeningAutoPlay = false;
        config.NotifyNearbyBroadcaster = true;
        config.NotifyNearbyBroadcasterAutoPlayOff = true;
        var listening = Create();

        listening.AddOrUpdatePair(1, "Alice", Track(AliceTrack));

        await TestWait.Assert(() => TestBootstrap.Chat.Messages.Length == 1,
            "one specific nearby notification");
        Assert.Contains("Auto-play is Off", TestBootstrap.Chat.Messages[0]);
        Assert.Contains(TestBootstrap.Chat.RichMessages[0].Payloads,
            payload => payload is UIForegroundPayload { ColorKey: 504 });
    }

    [Fact]
    public async Task Broadcasting_variant_can_be_enabled_independently()
    {
        config.NotifyNearbyBroadcaster = true;
        config.NotifyNearbyBroadcasterWhileBroadcasting = true;
        var listening = Create();
        listening.SetBroadcasting(true);

        listening.AddOrUpdatePair(1, "Alice", Track(AliceTrack));

        await TestWait.Assert(() => TestBootstrap.Chat.Messages.Length == 1,
            "broadcasting-specific nearby notification");
        Assert.Contains("you're currently broadcasting", TestBootstrap.Chat.Messages[0]);
    }

    [Fact]
    public async Task Selected_silent_playback_replaces_the_generic_nearby_notification()
    {
        config.ListeningMasterVolume = 0f;
        config.NotifyNearbyBroadcaster = true;
        config.NotifyMutedPlayback = true;
        var listening = Create();

        listening.AddOrUpdatePair(1, "Alice", Track(AliceTrack));

        await TestWait.Assert(() => TestBootstrap.Chat.Messages.Length == 1,
            "one silent-playback notification");
        Assert.Contains("master listening volume is set to zero", TestBootstrap.Chat.Messages[0]);
    }

    [Fact]
    public async Task A_selected_paused_pair_notifies_when_it_starts_while_muted()
    {
        config.ListeningMasterVolume = 0f;
        config.NotifyMutedPlayback = true;
        var listening = Create();
        listening.AddOrUpdatePair(1, "Alice", Track(AliceTrack, playing: false));
        await TestWait.Assert(() => listening.View.Count == 1, "paused pair is tracked");
        Assert.Empty(TestBootstrap.Chat.Messages);

        listening.AddOrUpdatePair(1, "Alice", Track(AliceTrack, playing: true, epoch: 2));

        await TestWait.Assert(() => TestBootstrap.Chat.Messages.Length == 1,
            "muted start notification");
        Assert.Contains("Alice started playing", TestBootstrap.Chat.Messages[0]);
    }

    [Fact]
    public async Task Track_change_notification_uses_the_applied_track_and_is_configurable()
    {
        config.NotifyListeningTrackChanged = true;
        var listening = Create();
        listening.AddOrUpdatePair(1, "Alice", Track(AliceTrack, title: "First"));
        await TestWait.Assert(() => engine.Snapshot.Path == AliceTrack, "first track is applied");
        TestBootstrap.Chat.Clear();

        listening.AddOrUpdatePair(1, "Alice", Track(NextTrack, epoch: 2, title: "Second"));

        await TestWait.Assert(() => engine.Snapshot.Path == NextTrack, "next track is applied");
        await TestWait.Assert(() => TestBootstrap.Chat.Messages.Length == 1,
            "track change notification");
        Assert.Contains("Now Playing: Alice is now playing Artist - Second", TestBootstrap.Chat.Messages[0]);
    }

    [Fact]
    public async Task Debug_loopback_uses_the_normal_nearby_notification()
    {
        config.NotifyNearbyBroadcaster = true;
        var listening = Create();
        listening.SetBroadcasting(true);

        listening.AddOrUpdatePair(ulong.MaxValue, "Debug Loopback", Track(AliceTrack));

        await TestWait.Assert(() => TestBootstrap.Chat.Messages.Length == 1,
            "loopback notification");
        Assert.Contains("Nearby Broadcast: Debug Loopback is playing Artist - Song",
            TestBootstrap.Chat.Messages[0]);
    }
}
