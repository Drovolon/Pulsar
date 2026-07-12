using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Pulsar.AudioHost.Playback;
using Pulsar.Common.Api;
using Pulsar.Tests.Fakes;
using Xunit;

namespace Pulsar.Tests;

public class FilePlayerTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("pulsar-fp-test-");

    public void Dispose() => dir.Delete(recursive: true);

    private string CreateWav(short amplitude = 0, string name = "t.wav", int samples = 44100)
    {
        var path = Path.Combine(dir.FullName, name);
        using var w = new WaveFileWriter(path, new WaveFormat(44100, 16, 1));
        var data = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            data[i * 2] = (byte)(amplitude & 0xFF);
            data[i * 2 + 1] = (byte)((amplitude >> 8) & 0xFF);
        }
        w.Write(data, 0, data.Length);
        return path;
    }

    // Regression test: stopping a never-started WASAPI device (paused load) raises no
    // PlaybackStopped, and the command loop hung on that event forever.
    [Fact]
    public async Task Stop_after_paused_load_does_not_wedge_the_player()
    {
        var wav = CreateWav();
        var player = new FilePlayer(() => new FakeWavePlayer());

        player.Load(wav, TimeSpan.Zero, playing: false);
        await TestWait.Assert(() => player.State == PlaybackState.Paused, "paused load is applied");

        player.Stop();
        await TestWait.Assert(() => player.State == PlaybackState.Stopped,
            "player wedged on stop after a paused load");

        // The command loop must still be alive: a follow-up load has to reach Playing.
        player.Load(wav, TimeSpan.Zero, playing: true);
        await TestWait.Assert(() => player.State == PlaybackState.Playing,
            "command loop is dead after the stop");

        // Bounded on purpose: a wedged player would never return, and an undisposed
        // player holds the wav open when Dispose deletes the temp dir.
        await TestWait.Within(player.DisposeAsync().AsTask(), "dispose after the anti-wedge checks");
    }

    [Fact]
    public async Task Full_transport_lifecycle_commits_state_at_each_step()
    {
        var wav = CreateWav();
        var device = new FakeWavePlayer();
        var player = new FilePlayer(() => device);

        player.Load(wav, TimeSpan.Zero, playing: true);
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "load reaches playing");
        Assert.Equal(PlaybackState.Playing, device.PlaybackState);

        player.Pause();
        await TestWait.Assert(() => player.State == PlaybackState.Paused, "pause lands");
        Assert.Equal(PlaybackState.Paused, device.PlaybackState);

        player.Resume();
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "resume lands");

        player.Stop();
        await TestWait.Assert(() => player.State == PlaybackState.Stopped, "stop lands");
        Assert.Null(player.Path);

        await player.DisposeAsync();
    }

    [Fact]
    public async Task Natural_end_fires_playback_ended_but_not_changed()
    {
        // "Stop" vs "track ended" is load-bearing: DirectoryPlayer auto-advances on
        // OnPlaybackEnded, so an explicit stop must not look like a track ending.
        var wav = CreateWav();
        var device = new FakeWavePlayer();
        var player = new FilePlayer(() => device);
        var changed = 0;
        var ended = new TaskCompletionSource<Pulsar.Common.Api.EndReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        player.OnChanged += (_, _) => Interlocked.Increment(ref changed);
        player.OnPlaybackEnded += (_, r) => ended.TrySetResult(r);

        player.Load(wav, TimeSpan.Zero, playing: true);
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "load reaches playing");
        var changesBeforeEnd = changed;

        device.SimulateNaturalEnd();

        var reason = await TestWait.Within(ended.Task, "playback-ended event");
        Assert.Equal(Pulsar.Common.Api.EndReason.Finished, reason);
        await TestWait.Assert(() => player.State == PlaybackState.Stopped, "natural end stops the player");
        Assert.Equal(changesBeforeEnd, changed); // natural end is not a user action

        await player.DisposeAsync();
    }

    [Fact]
    public async Task Unreadable_file_reports_a_failed_load()
    {
        var player = new FilePlayer(() => new FakeWavePlayer());
        var ended = new TaskCompletionSource<Pulsar.Common.Api.EndReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        player.OnPlaybackEnded += (_, r) => ended.TrySetResult(r);

        player.Load(Path.Combine(dir.FullName, "missing.wav"), TimeSpan.Zero, playing: true);

        Assert.Equal(Pulsar.Common.Api.EndReason.Failed,
            await TestWait.Within(ended.Task, "failed-load event"));
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.NotNull(player.LastError);

        await player.DisposeAsync();
    }

    [Fact]
    public async Task Seek_moves_and_clamps_the_position()
    {
        var wav = CreateWav(); // 1 second long
        var player = new FilePlayer(() => new FakeWavePlayer());

        player.Load(wav, TimeSpan.Zero, playing: false);
        await TestWait.Assert(() => player.State == PlaybackState.Paused, "paused load is applied");

        player.Seek(TimeSpan.FromMilliseconds(500));
        await TestWait.Assert(
            () => player.Position is { } p && Math.Abs((p.Current - TimeSpan.FromMilliseconds(500)).TotalMilliseconds) < 50,
            "position lands near the seek target");

        // Seeking past the end clamps to the track length rather than exploding.
        player.Seek(TimeSpan.FromSeconds(30));
        await TestWait.Assert(
            () => player.Position is { } p && p.Current == p.Total,
            "overshoot clamps to the end");

        // The player survives all of it.
        player.Resume();
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "resume after seeks");
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Volume_set_before_load_shapes_the_audio_of_the_next_track()
    {
        // Volume must survive across loads (the mixer slider isn't re-dragged per song).
        // Verify through the actual audio path: samples come out scaled.
        var wav = CreateWav(amplitude: 16384); // ~0.5 full scale
        var device = new FakeWavePlayer();
        var player = new FilePlayer(() => device);

        player.Volume(0.5f);
        player.Load(wav, TimeSpan.Zero, playing: true);
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "load reaches playing");

        var provider = device.Provider!;
        Assert.Equal(32, provider.WaveFormat.BitsPerSample); // float pipeline
        var bytes = new byte[4096];
        var read = provider.Read(bytes);
        Assert.True(read > 0, "audio flows through the pipeline");

        // 16384/32768 = 0.5 amplitude, halved again by the volume stage.
        var sample = BitConverter.ToSingle(bytes, read - 4);
        Assert.Equal(0.25f, sample, 2);

        await player.DisposeAsync();
    }

    [Fact]
    public async Task LoadBytes_plays_audio_from_memory_under_the_display_path()
    {
        // The SCD path: plugin-side extraction ships raw bytes; the display path is
        // the .scd's own path so sync identity keys on it.
        var bytes = File.ReadAllBytes(CreateWav());
        var player = new FilePlayer(() => new FakeWavePlayer());

        player.LoadBytes(@"D:\mods\song.scd", bytes, TimeSpan.Zero, playing: true);

        await TestWait.Assert(() => player.State == PlaybackState.Playing, "bytes load plays");
        Assert.Equal(@"D:\mods\song.scd", player.Path);
        await TestWait.Assert(
            () => player.Position is { } p && Math.Abs((p.Total - TimeSpan.FromSeconds(1)).TotalMilliseconds) < 100,
            "duration comes from the decoded bytes");
        await player.DisposeAsync();
    }

    [Fact]
    public async Task A_device_failure_mid_playback_reports_failed()
    {
        var wav = CreateWav();
        var device = new FakeWavePlayer();
        var player = new FilePlayer(() => device);
        EndReason? ended = null;
        player.OnPlaybackEnded += (_, r) => ended = r;

        player.Load(wav, TimeSpan.Zero, playing: true);
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "playing");

        device.SimulateNaturalEnd(new InvalidOperationException("device died"));

        await TestWait.Assert(() => ended == EndReason.Failed, "failure surfaces as Failed");
        Assert.Equal(PlaybackState.Stopped, player.State);
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Loading_over_a_playing_track_swaps_devices_cleanly()
    {
        var wav = CreateWav();
        var devices = new List<FakeWavePlayer>();
        var player = new FilePlayer(() => { var d = new FakeWavePlayer(); devices.Add(d); return d; });

        player.Load(wav, TimeSpan.Zero, playing: true);
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "first track playing");

        player.Load(wav, TimeSpan.Zero, playing: true); // no intervening Stop
        await TestWait.Assert(() => devices.Count == 2, "a fresh device is built");
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "second track playing");
        Assert.Equal(PlaybackState.Stopped, devices[0].PlaybackState); // old device was stopped
        await player.DisposeAsync();
    }

    [Fact]
    public async Task A_negative_seek_clamps_to_the_start()
    {
        var wav = CreateWav();
        var player = new FilePlayer(() => new FakeWavePlayer());
        player.Load(wav, TimeSpan.FromMilliseconds(500), playing: false);
        await TestWait.Assert(() => player.State == PlaybackState.Paused, "paused load");

        player.Seek(TimeSpan.FromSeconds(-5));
        await TestWait.Assert(() => player.Position is { Current: var c } && c == TimeSpan.Zero,
            "negative seek clamps to zero");
        await player.DisposeAsync();
    }

    [Fact]
    public async Task Volume_changes_mid_playback_shape_the_live_audio()
    {
        var wav = CreateWav(amplitude: 16384); // ~0.5 full scale
        var device = new FakeWavePlayer();
        var player = new FilePlayer(() => device);

        player.Load(wav, TimeSpan.Zero, playing: true);
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "playing");

        player.Volume(0.5f);
        // Commands apply in order: once the seek below is observable, the volume is too.
        player.Seek(TimeSpan.Zero);
        await TestWait.Assert(() => player.Position is { Current: var c } && c == TimeSpan.Zero, "marker seek lands");

        var bytes = new byte[4096];
        var read = device.Provider!.Read(bytes);
        Assert.True(read > 0, "audio flows");
        var sample = BitConverter.ToSingle(bytes, read - 4);
        Assert.Equal(0.25f, sample, 2); // 0.5 amplitude * 0.5 volume
        await player.DisposeAsync();
    }

    [Fact]
    public async Task No_op_transport_commands_raise_no_events()
    {
        var wav = CreateWav();
        var player = new FilePlayer(() => new FakeWavePlayer());
        var events = 0;
        player.OnChanged += (_, _) => Interlocked.Increment(ref events);

        player.Load(wav, TimeSpan.Zero, playing: true);
        await TestWait.Assert(() => player.State == PlaybackState.Playing, "playing");
        var seen = events;

        player.Resume(); // already playing: no-op
        await Task.Delay(150);
        Assert.Equal(seen, events);

        player.Stop();
        await TestWait.Assert(() => player.State == PlaybackState.Stopped, "stopped");
        seen = events;

        player.Pause(); // already stopped: no-op
        await Task.Delay(150);
        Assert.Equal(seen, events);
        await player.DisposeAsync();
    }
}
