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
    private readonly SyncPrep prep;
    private readonly BroadcastManager broadcast;
    private readonly ListeningManager listening;
    private readonly IpcProvider ipc;
    private readonly DebugLoopbackController debugLoopback;
    private readonly ApplicationCoordinator coordinator;

    public IpcProviderTests()
    {
        prep = new SyncPrep(new CacheManager(Path.Combine(dir.FullName, "cache")), service);
        var config = TestData.QuietConfiguration();
        broadcast = new BroadcastManager(new FakeRemoteEngine(), null!, prep, config);
        listening = new ListeningManager(listenEngine, config);
        ipc = new IpcProvider(gates.Gates, listening, broadcast);
        ipc.Prepare();
        debugLoopback = new DebugLoopbackController(ipc);
        coordinator = new ApplicationCoordinator(broadcast, listening, ipc, debugLoopback);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await coordinator.DisposeAsync();
        ipc.Dispose();
        await listening.DisposeAsync();
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
    public async Task The_wire_uses_named_records_and_stops_with_null()
    {
        Assert.Equal(PulsarApiVersions.Current, gates.ApiVersion.Func!());

        var track = TestData.CreateTrack(dir, "song.flac");
        await broadcast.SetSource(new FakeMusicSource { Current = TestData.Snap(track) });
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

        await broadcast.SetSource(null);
        await TestWait.Assert(() =>
            gates.PlayerDataChanged.Sent[^1] is [null],
            "a stop goes out as null player data");
    }
}
