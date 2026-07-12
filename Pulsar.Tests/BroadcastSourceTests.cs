using System;
using System.IO;
using System.Threading.Tasks;
using Pulsar.Broadcast;
using Pulsar.Broadcast.Prepare;
using Pulsar.Listening;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// BroadcastManager's source lifecycle beyond the manifest pipeline: the prefetch
/// timer (lead + final checkpoints, disarm rules), overlapping source switches, and
/// the folder/mod loaders. The prefetch-timer tests mutate the source's Current
/// WITHOUT raising an event - only the timer reads live state, so a prefetch for a
/// track no event ever carried must have come from it.
/// </summary>
public class BroadcastSourceTests : IAsyncLifetime
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-bsrc-test-");
    private readonly ControllablePrepareService service = new();
    private readonly FakeRemoteEngine engine = new();
    private readonly FakeModResolver resolver = new();
    private readonly SyncPrep prep;
    private readonly BroadcastManager broadcast;

    private Configuration config = new();

    public BroadcastSourceTests()
    {
        prep = new SyncPrep(new CacheManager(Path.Combine(dir.FullName, "cache")), service);
        broadcast = new BroadcastManager(engine, resolver, prep, () => config);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await broadcast.DisposeAsync();
        await prep.DisposeAsync();
        dir.Delete(recursive: true);
    }

    private sealed class FakeModResolver : IModResolver
    {
        public string? Result;
        public string? ResolveModDirectory(string modDirectoryName) => Result;
    }

    private string Track(string name) => TestData.CreateTrack(dir, name);

    /// <summary>A snapshot whose duration/next the prefetch timer reads.</summary>
    private static SourceSnapshot TimedSnap(string file, long durationMs, string? next = null, bool playing = true)
        => new(file, next, playing, TimeSpan.Zero, DateTimeOffset.UtcNow,
               new TrackMeta { OriginalFileName = Path.GetFileName(file), DurationMs = durationMs });

    // ---- prefetch timer ---------------------------------------------------------

    [Fact]
    public async Task The_lead_checkpoint_prefetches_the_live_next_track()
    {
        // dur 1000ms, lead 700ms -> lead tick ~300ms after arming.
        config = new Configuration { PrefetchLeadMs = 700, PrefetchFinalMs = 100 };
        var current = Track("current.flac");
        var next = Track("next.flac");

        var source = new FakeMusicSource { Current = TimedSnap(current, durationMs: 1000) };
        await broadcast.SetSource(source);
        Assert.True(await service.WaitForPrepare(current), "active track preps");
        Assert.False(await service.WaitForPrepare(next, TimeSpan.FromMilliseconds(150)),
            "nothing can have prefetched 'next' yet - no event ever carried it");

        // Visible ONLY to the timer: no event is raised.
        source.Current = TimedSnap(current, durationMs: 1000, next: next);

        Assert.True(await service.WaitForPrepare(next), "the lead tick prefetched the next track");
    }

    [Fact]
    public async Task Inside_the_lead_window_the_final_checkpoint_still_fires()
    {
        // lead (5s) > duration (1s): arming lands INSIDE the lead window, so the
        // FINAL checkpoint is scheduled directly (~700ms: remaining 1000 - final 300).
        config = new Configuration { PrefetchLeadMs = 5000, PrefetchFinalMs = 300 };
        var current = Track("current.flac");
        var next = Track("next.flac");

        var source = new FakeMusicSource { Current = TimedSnap(current, durationMs: 1000) };
        await broadcast.SetSource(source);
        Assert.True(await service.WaitForPrepare(current), "active track preps");

        source.Current = TimedSnap(current, durationMs: 1000, next: next); // no event

        Assert.True(await service.WaitForPrepare(next), "the final tick prefetched the next track");
    }

    [Fact]
    public async Task A_paused_source_disarms_the_prefetch_timer()
    {
        // If the pause guard regresses, these values arm the erroneous lead tick in
        // ~100ms (1000ms remaining - 900ms lead), comfortably inside the negative wait.
        config = new Configuration { PrefetchLeadMs = 900, PrefetchFinalMs = 50 };
        var current = Track("current.flac");
        var next = Track("next.flac");

        var source = new FakeMusicSource { Current = TimedSnap(current, 1000, playing: false) };
        await broadcast.SetSource(source);

        source.Current = TimedSnap(current, 1000, next: next, playing: false); // no event
        await Task.Delay(400);
        Assert.DoesNotContain(next, service.PrepareCalls); // paused: no tick may fire
    }

    [Fact]
    public async Task Unknown_duration_disarms_the_prefetch_timer()
    {
        config = new Configuration { PrefetchLeadMs = 100, PrefetchFinalMs = 50 };
        var current = Track("current.flac");
        var next = Track("next.flac");

        var source = new FakeMusicSource { Current = TimedSnap(current, durationMs: 0) };
        await broadcast.SetSource(source);
        Assert.True(await service.WaitForPrepare(current), "active track preps");

        source.Current = TimedSnap(current, durationMs: 0, next: next); // no event
        await Task.Delay(400);
        Assert.DoesNotContain(next, service.PrepareCalls); // unknown duration: no tick may fire
    }

    // ---- overlapping switches ---------------------------------------------------

    [Fact]
    public async Task Overlapping_switches_serialize_and_the_last_caller_wins()
    {
        var a = new FakeMusicSource
        {
            Current = TestData.Snap(Track("one.flac")),
            DisposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        await broadcast.SetSource(a);

        var t3 = Track("three.flac");
        var b = new FakeMusicSource { Current = TestData.Snap(Track("two.flac")) };
        var c = new FakeMusicSource { Current = TestData.Snap(t3) };

        var toB = broadcast.SetSource(b); // parks: a's teardown is gated
        await Task.Delay(50);
        Assert.False(toB.IsCompleted, "switch to b waits on a's teardown");
        var toC = broadcast.SetSource(c); // queues behind it on switchLock

        a.DisposeGate.TrySetResult();
        await TestWait.Within(toB, "switch to b");
        await TestWait.Within(toC, "switch to c");

        Assert.True(a.Disposed, "a torn down");
        Assert.True(b.Disposed, "b installed then torn down by c's switch");
        Assert.False(c.Disposed, "c is live");
        Assert.Equal(t3, broadcast.CurrentSnapshot?.FilePath);
    }

    // ---- loaders ------------------------------------------------------------------

    private DirectoryInfo CreateFolder(params string[] tracks)
    {
        var folder = Directory.CreateDirectory(Path.Combine(dir.FullName, "folder"));
        foreach (var t in tracks) TestData.CreateTrack(folder, t);
        return folder;
    }

    [Fact]
    public async Task LoadFolder_installs_a_playable_jukebox()
    {
        var folder = CreateFolder("a.mp3", "b.mp3");
        await broadcast.LoadFolder(folder.FullName);

        Assert.NotNull(broadcast.ActiveJukebox);
        broadcast.ActiveJukebox!.Player.Play();
        await TestWait.Assert(() => broadcast.CurrentSnapshot is { IsPlaying: true },
            "the jukebox snapshot goes live through the manager");
    }

    [Fact]
    public async Task LoadFolder_on_a_missing_directory_installs_nothing()
    {
        await broadcast.LoadFolder(Path.Combine(dir.FullName, "does-not-exist"));
        Assert.Null(broadcast.ActiveJukebox);
        Assert.Null(broadcast.CurrentSnapshot);
    }

    [Fact]
    public async Task LoadMod_resolves_the_mod_directory_and_plays()
    {
        resolver.Result = CreateFolder("mod-song.mp3").FullName;
        await broadcast.LoadMod("CoolMod");

        Assert.NotNull(broadcast.ActiveJukebox);
        broadcast.ActiveJukebox!.Player.Play();
        await TestWait.Assert(() => broadcast.CurrentSnapshot is { IsPlaying: true }, "mod jukebox plays");
    }

    [Fact]
    public async Task LoadMod_failure_keeps_the_current_source_on_air()
    {
        var live = Track("live.flac");
        await broadcast.SetSource(new FakeMusicSource { Current = TestData.Snap(live) });

        resolver.Result = null; // Penumbra unavailable / mod missing
        await broadcast.LoadMod("MissingMod");

        Assert.Equal(live, broadcast.CurrentSnapshot?.FilePath); // failure must not stop the show
    }
}
