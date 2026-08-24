using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Pulsar.Api;
using Pulsar.Broadcast;
using Pulsar.Broadcast.Prepare;
using Pulsar.Ipc;
using Pulsar.Listening;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// The wire-contract facts external plugins depend on: malformed input is contained,
/// the JSON is camelCase (OUR naming-policy choice, not the serializer's), hashes occupy
/// their named record fields, and a stop is null. Everything else is covered by
/// LoopbackFlowTests riding the real IPC loop - deliberately no glue tests here.
/// </summary>
public class IpcProviderTests : IAsyncLifetime
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-ipc-test-");
    private readonly ControllablePrepareService service = new();
    private readonly FakeRemoteEngine listenEngine = new();
    private readonly FakeIpcGates gates = new();
    private readonly Configuration config = TestData.QuietConfiguration();
    private readonly PrefetchTiming prefetchTiming = new();
    private readonly SyncPrep prep;
    private readonly BroadcastManager broadcast;
    private readonly ListeningManager listening;
    private readonly IpcProvider ipc;
    private readonly DebugLoopbackController debugLoopback;
    private readonly ApplicationCoordinator coordinator;
    private readonly FakeGameBgmControl gameBgm = new();

    public IpcProviderTests()
    {
        prep = new SyncPrep(new CacheManager(Path.Combine(dir.FullName, "cache")), service);
        broadcast = new BroadcastManager(new FakeRemoteEngine(), null!, prep, config, prefetchTiming);
        listening = new ListeningManager(listenEngine, config);
        ipc = new IpcProvider(gates.Gates, listening, broadcast);
        ipc.Prepare();
        debugLoopback = new DebugLoopbackController(ipc);
        coordinator = new ApplicationCoordinator(
            broadcast,
            listening,
            ipc,
            debugLoopback,
            new ListeningNotifier(config),
            new BgmMuter(config, gameBgm));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await coordinator.DisposeAsync();
        ipc.Dispose();
        await prep.DisposeAsync();
        dir.Delete(recursive: true);
    }

    [Fact]
    public void Malformed_peer_cursor_is_contained()
    {
        // A buggy peer must never explode into Dalamud's IPC dispatch.
        var ex = Record.Exception(() =>
            gates.SetPlayerData.Action!(7UL, @"C:\x.opus", null, "{this is not json"));
        Assert.Null(ex);
        Assert.Empty(listening.View); // and no half-built pair materializes
    }

    [Fact]
    public async Task ClearPlayerData_is_the_explicit_delete_operation()
    {
        var payload = JsonSerializer.Serialize(new PulsarCursor
        {
            IsPlaying = true,
            AsOfUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CursorEpoch = 1,
        });
        gates.SetPlayerData.Action!(7UL, @"C:\x.opus", null, payload);
        await TestWait.Assert(() => listening.View.Any(p => p.Ident == 7UL),
            "SetPlayerData adds the pair");

        gates.ClearPlayerData.Action!(7UL);
        await TestWait.Assert(() => listening.View.All(p => p.Ident != 7UL),
            "ClearPlayerData removes the pair");
    }

    [Fact]
    public async Task Listening_outputs_are_routed_to_the_bgm_muter()
    {
        var payload = JsonSerializer.Serialize(new PulsarCursor
        {
            IsPlaying = true,
            AsOfUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CursorEpoch = 1,
        });

        gates.SetPlayerData.Action!(7UL, @"C:\x.opus", null, payload);
        await TestWait.Assert(() => gameBgm.Muted, "listening mutes game BGM");

        gates.ClearPlayerData.Action!(7UL);
        await TestWait.Assert(() => !gameBgm.Muted, "stopping listening restores game BGM");
    }

    [Fact]
    public async Task The_wire_uses_named_records_and_stops_with_null()
    {
        Assert.Equal(PulsarApiVersions.Current, gates.ApiVersion.Func!());

        var track = TestData.CreateTrack(dir, "song.flac");
        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = TestData.Snap(track) });
        await TestWait.Assert(() => gates.PlayerDataChanged.Sent.Count > 0, "manifest reaches the wire");

        // camelCase is OUR PropertyNamingPolicy choice - no compile error guards it,
        // and every external consumer parses these exact keys.
        var changed = Assert.IsType<PulsarPlayerData>(gates.PlayerDataChanged.Sent[^1][0]);
        Assert.Equal(ControllablePrepareService.Blake3Hash, changed.Current.Blake3Hash);
        Assert.Equal(ControllablePrepareService.Sha1Hash, changed.Current.Sha1Hash);
        Assert.Null(changed.Prefetch);
        var cursorJson = changed.Payload;
        Assert.Contains("\"positionMs\"", cursorJson);
        Assert.Contains("\"cursorEpoch\"", cursorJson);
        Assert.DoesNotContain("\"PositionMs\"", cursorJson);

        var queried = gates.GetPlayerData.Func!();
        Assert.NotNull(queried);
        Assert.Equal(ControllablePrepareService.Blake3Hash, queried.Current.Blake3Hash);
        Assert.Equal(ControllablePrepareService.Sha1Hash, queried.Current.Sha1Hash);
        Assert.Equal(cursorJson, queried.Payload);

        broadcast.SetOnAir(false);
        await TestWait.Assert(() =>
            gates.PlayerDataChanged.Sent[^1] is [null],
            "a stop goes out as null player data");
    }

    [Fact]
    public async Task A_completed_prefetch_is_pushed_and_queryable_with_an_unchanged_payload()
    {
        // TestData.Snap has 55s remaining, so this fires about 200ms after arming.
        prefetchTiming.LeadMs = 54_800;
        prefetchTiming.FinalMs = 100;
        var current = TestData.CreateTrack(dir, "current.flac");
        var next = TestData.CreateTrack(dir, "next.flac");

        await broadcast.BroadcastFromForTests(BroadcastProvider.Local, new FakeMusicSource { Current = TestData.Snap(current, next: next) });
        await TestWait.Assert(
            () => gates.PlayerDataChanged.Sent.LastOrDefault() is [PulsarPlayerData { Prefetch: null }],
            "the current-only manifest reaches IPC");
        var before = Assert.IsType<PulsarPlayerData>(gates.PlayerDataChanged.Sent[^1][0]);

        Assert.False(await service.WaitForPrepare(next, TimeSpan.FromMilliseconds(100)),
            "prefetch does not start at the track switch");
        Assert.True(await service.WaitForPrepare(next), "the checkpoint starts prefetch");
        await TestWait.Assert(
            () => gates.PlayerDataChanged.Sent.LastOrDefault() is [PulsarPlayerData { Prefetch: not null }],
            "the prefetch-only update reaches IPC");

        var pushed = Assert.IsType<PulsarPlayerData>(gates.PlayerDataChanged.Sent[^1][0]);
        Assert.Equal(before.Current, pushed.Current);
        Assert.Equal(before.Payload, pushed.Payload);
        Assert.Equal(pushed, gates.GetPlayerData.Func!());
        Assert.Equal(ControllablePrepareService.Blake3Hash, pushed.Prefetch!.Blake3Hash);
        Assert.Equal(ControllablePrepareService.Sha1Hash, pushed.Prefetch.Sha1Hash);
    }
}
