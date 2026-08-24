using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Pulsar.Listening;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

public sealed class ListeningOutputTests : IAsyncLifetime
{
    private const string TrackPath = @"C:\sync\alice.opus";

    private readonly FakeRemoteEngine engine = new();
    private readonly ConcurrentQueue<ListeningOutput> outputs = new();
    private readonly ListeningManager listening;
    private readonly Task outputLoop;

    public ListeningOutputTests()
    {
        listening = new ListeningManager(engine, TestData.QuietConfiguration());
        outputLoop = CollectOutputs();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await listening.DisposeAsync();
        await outputLoop;
    }

    [Fact]
    public async Task Playing_and_paused_engine_states_emit_listening_transitions()
    {
        listening.AddOrUpdatePair(1, "Alice", Pair(true));
        await TestWait.Assert(() => Changes().SequenceEqual([true]), "listening starts");

        listening.AddOrUpdatePair(1, "Alice", Pair(false, 2));
        await TestWait.Assert(() => Changes().SequenceEqual([true, false]), "listening stops on pause");
    }

    [Fact]
    public async Task Listening_stop_is_emitted_only_after_the_engine_has_stopped()
    {
        listening.AddOrUpdatePair(1, "Alice", Pair(true));
        await TestWait.Assert(() => Changes().SequenceEqual([true]), "listening starts");

        var stopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Stall = op => op == "Stop" ? stopGate.Task : null;
        try
        {
            listening.SetAutoPlay(false);
            Assert.True(await engine.WaitForCall("Stop"), "the stop RPC begins");
            Assert.Equal([true], Changes());
        } finally
        {
            stopGate.TrySetResult();
        }

        await TestWait.Assert(() => Changes().SequenceEqual([true, false]), "listening stops after the engine confirms it");
    }

    [Fact]
    public async Task Muted_pulsar_playback_still_counts_as_listening()
    {
        listening.SetMasterMuted(true);
        listening.AddOrUpdatePair(1, "Alice", Pair(true));

        await TestWait.Assert(() => Changes().Contains(true), "muted playback starts listening");
        Assert.Equal(0f, engine.LastVolume);
    }

    [Fact]
    public async Task Detecting_a_source_with_autoplay_off_does_not_begin_listening()
    {
        listening.SetAutoPlay(false);
        listening.AddOrUpdatePair(1, "Alice", Pair(true));

        await TestWait.Assert(() => listening.View.Count == 1, "source is detected");
        Assert.Empty(Changes());
    }

    private PairData Pair(bool playing, int epoch = 1) =>
        new(TrackPath, TimeSpan.FromSeconds(5), playing, DateTimeOffset.UtcNow, epoch, null);

    private bool[] Changes() => outputs.OfType<ListeningOutput.ListeningChanged>().Select(change => change.Value).ToArray();

    private async Task CollectOutputs()
    {
        await foreach (var output in listening.Outputs.ReadAllAsync())
            outputs.Enqueue(output);
    }
}
