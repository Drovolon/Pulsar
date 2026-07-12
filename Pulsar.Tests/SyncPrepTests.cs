using System;
using System.IO;
using System.Threading.Tasks;
using Pulsar.Broadcast.Prepare;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

/// <summary>
/// SyncPrep is the serialized prep worker: one transcode at a time, active requests
/// preempt prefetches, results are cached for the manifest probe (TryGet).
/// </summary>
public class SyncPrepTests : IAsyncLifetime
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-sp-test-");
    private readonly ControllablePrepareService service = new();
    private SyncPrep? prep;

    private SyncPrep Create(long cacheCapBytes = 1L << 26) => prep = new SyncPrep(
        new CacheManager(Path.Combine(dir.FullName, "cache")) { CacheCapBytes = cacheCapBytes }, service);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (prep is not null) await prep.DisposeAsync();
        dir.Delete(recursive: true);
    }

    private string CreateTrack(string name) => TestData.CreateTrack(dir, name);

    private static Task<PrepResult> Within(Task<PrepResult> task, string because)
        => TestWait.Within(task, because);

    [Fact]
    public async Task Prep_after_dispose_completes_preempted_instead_of_hanging()
    {
        var sp = Create();
        var track = CreateTrack("late.flac");
        await sp.DisposeAsync();

        // Reachable during unload: a source event already past BroadcastManager's fences
        // calls into a disposed SyncPrep; the worker is gone, so enqueueing would hang.
        var result = await Within(sp.PrepareActive(track), "prep requested after dispose");
        Assert.IsType<PrepResult.Preempted>(result);
    }

    [Fact]
    public async Task Cache_served_hits_keep_the_artifact_lru_warm()
    {
        var sp = Create();
        var track = CreateTrack("looped.flac");
        var first = Assert.IsType<PrepResult.Successful>(
            await Within(sp.PrepareActive(track), "initial prep"));

        // A cache-served hit must refresh the access time, or LRU eviction sees the
        // DJ's hottest artifact as the coldest (nothing else touches the file).
        var stale = DateTime.UtcNow - TimeSpan.FromHours(6);
        File.SetLastAccessTimeUtc(first.PreparedFilePath, stale);

        Assert.IsType<PrepResult.Successful>(
            await Within(sp.PrepareActive(track), "cache-served replay"));

        Assert.True(File.GetLastAccessTimeUtc(first.PreparedFilePath) > stale + TimeSpan.FromHours(1),
            "the replay refreshes the artifact's LRU age");
    }

    [Fact]
    public async Task An_attach_promoted_active_prep_pins_its_artifact_against_eviction()
    {
        // Tiny cap: every eviction pass wants to delete everything unpinned.
        var sp = Create(cacheCapBytes: 1);
        service.ArtifactBytes = 1024;

        var trackA = CreateTrack("a.flac");
        var gate = service.GateFor(trackA);
        var prefetch = sp.PreparePrefetch(trackA);
        Assert.True(await service.WaitForPrepare(trackA), "worker starts the prefetch");

        // The DJ's cursor lands mid-transcode: the request attaches to the running
        // prefetch and promotes it - eviction must treat the artifact as on-air.
        var active = sp.PrepareActive(trackA);
        gate.Open();
        var ok = Assert.IsType<PrepResult.Successful>(await Within(active, "the promoted prep"));
        await Within(prefetch, "the original prefetch");

        // A later prefetch completes and runs an over-cap eviction pass.
        var trackB = CreateTrack("b.flac");
        Assert.IsType<PrepResult.Successful>(await Within(sp.PreparePrefetch(trackB), "a later prefetch"));

        Assert.True(File.Exists(ok.PreparedFilePath), "the on-air artifact survives eviction");
    }

    // The track changes to a file already queued as a prefetch. The active request must
    // complete - PrepareThenReemit awaits it to re-emit the manifest.
    [Fact]
    public async Task Active_request_for_an_already_queued_prefetch_completes()
    {
        var sp = Create();
        var current = CreateTrack("current.flac");
        var next = CreateTrack("next.flac");
        var currentGate = service.GateFor(current);
        service.GateFor(next);

        var currentPrep = sp.PrepareActive(current);
        Assert.True(await service.WaitForPrepare(current), "worker starts on the current track");

        var prefetch = sp.PreparePrefetch(next);   // queued: worker is busy
        var active = sp.PrepareActive(next);       // track changed to the prefetched file

        currentGate.Open();
        service.GateFor(next).Open();

        Assert.IsType<PrepResult.Successful>(await Within(active, "the active request for the prefetched track"));
        await Within(prefetch, "the superseded prefetch request");
        await Within(currentPrep, "the preempted original prep");
    }

    [Fact]
    public async Task Transient_failure_recovers_on_the_next_attempt()
    {
        var sp = Create();
        var track = CreateTrack("flaky.flac");
        service.GateFor(track).Fail(new IOException("file locked by another process"));

        var first = await Within(sp.PrepareActive(track), "first (failing) prep");
        Assert.IsType<PrepResult.Failed>(first);

        // The lock is gone; the DJ plays the track again.
        service.Regate(track).Open();
        var second = await Within(sp.PrepareActive(track), "second prep");

        Assert.IsType<PrepResult.Successful>(second);
        Assert.True(sp.TryGet(track, out var cached), "result is cached");
        Assert.IsType<PrepResult.Successful>(cached); // NOT the stale failure
    }

    [Fact]
    public async Task Connection_loss_is_not_cached_as_a_permanent_failure()
    {
        var sp = Create();
        var track = CreateTrack("song.flac");
        service.GateFor(track).Fail(new StreamJsonRpc.ConnectionLostException("host died"));

        var result = await Within(sp.PrepareActive(track), "prep against a dead host");

        Assert.IsType<PrepResult.Failed>(result);
        Assert.False(sp.TryGet(track, out _), "a dead transcode host must not poison the track");
    }

    [Fact]
    public async Task Active_request_preempts_a_running_prefetch()
    {
        var sp = Create();
        var nextTrack = CreateTrack("next.flac");
        var urgent = CreateTrack("urgent.flac");
        service.GateFor(nextTrack); // prefetch will block until cancelled

        var prefetch = sp.PreparePrefetch(nextTrack);
        Assert.True(await service.WaitForPrepare(nextTrack), "prefetch starts");

        // DJ skips to a different track: sync is waiting on this prep.
        var active = await Within(sp.PrepareActive(urgent), "the active request");

        Assert.IsType<PrepResult.Successful>(active);
        Assert.IsType<PrepResult.Preempted>(await Within(prefetch, "the cancelled prefetch"));
        Assert.False(sp.TryGet(nextTrack, out _), "a preempted prefetch must not be cached as a result");
    }

    [Fact]
    public async Task Repeated_prep_of_a_prepared_track_reuses_the_cached_result()
    {
        // PrepareThenReemit re-requests the active track on every cursor event; a 10s+
        // transcode must not run again when the result is known.
        var sp = Create();
        var track = CreateTrack("song.flac");

        Assert.IsType<PrepResult.Successful>(await Within(sp.PrepareActive(track), "first prep"));
        Assert.IsType<PrepResult.Successful>(await Within(sp.PrepareActive(track), "second prep"));

        Assert.Single(service.PrepareCalls);
    }

    // A fidgety DJ re-requests the SAME file on every pause/seek. Restarting the
    // transcode each time can starve it forever; duplicates must join the in-flight job.
    [Fact]
    public async Task Requests_for_the_file_already_being_prepared_join_the_running_job()
    {
        var sp = Create();
        var track = CreateTrack("long-transcode.flac");
        var gate = service.GateFor(track);

        var first = sp.PrepareActive(track);
        Assert.True(await service.WaitForPrepare(track), "transcode starts");

        var second = sp.PrepareActive(track);   // DJ paused mid-transcode
        var third = sp.PreparePrefetch(track);  // and something prefetches it too

        gate.Open();

        Assert.IsType<PrepResult.Successful>(await Within(first, "the original request"));
        Assert.IsType<PrepResult.Successful>(await Within(second, "the duplicate active request"));
        Assert.IsType<PrepResult.Successful>(await Within(third, "the duplicate prefetch request"));
        Assert.Single(service.PrepareCalls); // one transcode, not three
    }

    [Fact]
    public async Task Cached_active_request_still_preempts_stale_running_work()
    {
        // DJ skips from slow track Z back to already-cached track A: serving A from
        // cache must not leave Z's now-pointless transcode hogging the only worker.
        var sp = Create();
        var cached = CreateTrack("cached.flac");
        var stale = CreateTrack("stale.flac");
        Assert.IsType<PrepResult.Successful>(await Within(sp.PrepareActive(cached), "priming the cache"));

        service.GateFor(stale);
        var staleJob = sp.PrepareActive(stale);
        Assert.True(await service.WaitForPrepare(stale), "stale transcode starts");

        var fromCache = await Within(sp.PrepareActive(cached), "the cache-served request");

        Assert.IsType<PrepResult.Successful>(fromCache);
        Assert.IsType<PrepResult.Preempted>(await Within(staleJob, "the abandoned transcode"));
    }

    [Fact]
    public async Task Eviction_never_deletes_the_active_broadcasts_artifact()
    {
        // A completing prefetch must not LRU-evict the artifact the live manifest points at.
        var sp = Create(cacheCapBytes: 1000);
        service.ArtifactBytes = 600;
        var onAir = CreateTrack("on-air.flac");
        var next = CreateTrack("next.flac");

        var active = Assert.IsType<PrepResult.Successful>(
            await Within(sp.PrepareActive(onAir), "active prep"));
        File.SetLastAccessTimeUtc(active.PreparedFilePath, DateTime.UtcNow.AddHours(-1)); // oldest by LRU

        Assert.IsType<PrepResult.Successful>(
            await Within(sp.PreparePrefetch(next), "prefetch that pushes the cache over its cap"));

        Assert.True(File.Exists(active.PreparedFilePath),
            "the artifact the live manifest points at survived the prefetch's eviction pass");
    }

    [Fact]
    public async Task Evicted_transcode_is_prepared_again()
    {
        // A cached "Successful" pointing at an evicted file must not short-circuit -
        // the manifest would announce a ghost.
        var sp = Create();
        var track = CreateTrack("song.flac");

        var first = Assert.IsType<PrepResult.Successful>(await Within(sp.PrepareActive(track), "first prep"));
        File.Delete(first.PreparedFilePath); // simulate LRU eviction

        var second = await Within(sp.PrepareActive(track), "re-prep after eviction");

        Assert.IsType<PrepResult.Successful>(second);
        Assert.Equal(2, service.PrepareCalls.Count);
    }
}
