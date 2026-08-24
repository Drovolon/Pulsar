using System.Threading.Channels;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Pulsar.Common;
using Pulsar.Common.Api;
using Serilog;

namespace Pulsar.AudioHost.Playback;

internal sealed class ProgressProvider(IWaveProvider source, WaveStream clock, Action<PlaybackPosition> report)
    : IWaveProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<byte> buffer)
    {
        var n = source.Read(buffer);
        report(new PlaybackPosition(clock.CurrentTime, clock.TotalTime));
        return n;
    }
}

/// <summary>
/// FilePlayer uses NAudio to actually *play* audio files. Like, audible to the user.
/// It's a pretty simple API: play a file path, stop playing, adjust volume, seek to a position.
/// 
/// Exposes one ordered stream of state updates. Natural ends and failures include
/// the stopped state and end reason in the same update.
/// </summary>
public sealed class FilePlayer
{
    private readonly Channel<PlaybackCommand> mailbox =
        Channel.CreateUnbounded<PlaybackCommand>(new UnboundedChannelOptions { SingleReader = true });

    private readonly Task loop;
    private readonly Func<IWavePlayer> deviceFactory;

    public FilePlayer() : this(DefaultDevice) { }

    // used for tests, to avoid playing real audio while running tests
    // (though it would be amusing to hear fake tones and such... once.)
    public FilePlayer(Func<IWavePlayer> deviceFactory)
    {
        this.deviceFactory = deviceFactory;
        loop = ProcessAsync();
    }

    private static IWavePlayer DefaultDevice() => new WasapiPlayerBuilder().WithMmcssThreadPriority().Build();

    public void Load(string path, TimeSpan position, bool playing, long playbackId = 0) =>
        mailbox.Writer.TryWrite(new LoadCommand(path, position, playing, playbackId));

    public void LoadBytes(string displayPath, byte[] audioData, TimeSpan position, bool playing, long playbackId = 0) =>
        mailbox.Writer.TryWrite(new LoadBytesCommand(displayPath, audioData, position, playing, playbackId));

    public void Stop() => mailbox.Writer.TryWrite(new StopCommand());
    public void Pause() => mailbox.Writer.TryWrite(new PauseCommand());
    public void Resume() => mailbox.Writer.TryWrite(new ResumeCommand());
    public void Volume(float volume) => mailbox.Writer.TryWrite(new VolumeCommand(volume));
    public void Seek(TimeSpan position) => mailbox.Writer.TryWrite(new SeekCommand(position));

    private EngineSnapshot published = new(PlaybackState.Stopped, null, null, null, DateTimeOffset.UtcNow);
    private long nextSequence;

    /// <summary>The last state published by the player.</summary>
    public EngineSnapshot Snapshot => Volatile.Read(ref published);

    public event EventHandler<EngineSnapshot>? OnUpdated;

    private void RaiseUpdated(EngineSnapshot snapshot)
    {
        try
        {
            OnUpdated?.Invoke(this, snapshot);
        }
        catch (Exception e)
        {
            Log.Error(e, "OnUpdated subscriber threw");
        }
    }

    private static PlaybackPosition ApplySeek(WaveStream reader, TimeSpan to)
    {
        reader.CurrentTime = to < TimeSpan.Zero ? TimeSpan.Zero : to > reader.TotalTime ? reader.TotalTime : to;
        return new PlaybackPosition(reader.CurrentTime, reader.TotalTime);
    }

    private EngineSnapshot Update(Func<EngineSnapshot, EngineSnapshot> transform)
    {
        while (true)
        {
            var current = Snapshot;
            var next = transform(current) with
            {
                Sequence = Interlocked.Increment(ref nextSequence),
            };
            if (ReferenceEquals(Interlocked.CompareExchange(ref published, next, current), current))
                return next;
        }
    }

    private void ReportPosition(long playbackId, PlaybackPosition position)
    {
        while (true)
        {
            var current = Snapshot;
            if (current.PlaybackId != playbackId || current.State == PlaybackState.Stopped)
                return;
            var next = current with
            {
                Position = position,
                ObservedAt = DateTimeOffset.UtcNow,
                Sequence = Interlocked.Increment(ref nextSequence),
            };
            if (ReferenceEquals(Interlocked.CompareExchange(ref published, next, current), current))
                return;
        }
    }

    private async Task Unload(IWavePlayer? device, WaveStream? reader, TaskCompletionSource? trackEnded, bool started)
    {
        Log.Debug("Unload enter (device={device}, started={started})", device is null ? "null" : "present", started);
        if (device is null) return;
        try
        {
            device.Stop();
            Log.Debug("Unload: Stop() returned, awaiting trackEnded");
            // A never-started device has no play thread: Stop() is a no-op and
            // PlaybackStopped will never fire, so awaiting it would wedge the loop.
            if (trackEnded is not null && started) await trackEnded.Task;
            Log.Debug("Unload: trackEnded done, disposing");
        }
        catch (Exception e)
        {
            ReportError(e, "Playback error");
        } finally
        {
            device?.Dispose();
            reader?.Dispose();
        }
    }

    private async Task ProcessAsync()
    {
        IWavePlayer? device = null;
        WaveStream? reader = null;
        VolumeSampleProvider? volumeProvider = null;
        TaskCompletionSource? trackEnded = null;
        var deviceStarted = false; // whether Play() ever ran on the current device
        var volume = 1.0f;

        var next = mailbox.Reader.ReadAsync().AsTask();

        try
        {
            // Dispose() closes the mailbox, which will make `next` throw ChannelClosedException,
            // which breaks this loop - it's not truly infinite.
            while (true)
            {
                if (trackEnded is not null && await Task.WhenAny(next, trackEnded.Task) == trackEnded.Task)
                {
                    var reason = trackEnded.Task.IsFaulted ? EndReason.Failed : EndReason.Finished;
                    var endedPlaybackId = Snapshot.PlaybackId;
                    Log.Debug("Playback ended ({reason})", reason);
                    await UnloadAndReset();
                    var terminal = Update(current => current with
                    {
                        State = PlaybackState.Stopped,
                        Path = null,
                        Position = null,
                        ObservedAt = DateTimeOffset.UtcNow,
                        TerminalReason = reason,
                    });
                    // The playback identity is captured before unload; Update preserves it.
                    if (terminal.PlaybackId == endedPlaybackId) RaiseUpdated(terminal);
                    continue;
                }

                PlaybackCommand cmd;
                try
                {
                    cmd = await next;
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                next = mailbox.Reader.ReadAsync().AsTask();

                if (cmd is not VolumeCommand) // these are way too chatty
                    Log.Debug("cmd={cmd}  trackEnded={ended}", cmd,
                              trackEnded is null ? "null" : trackEnded.Task.Status.ToString());

                try
                {
                    switch (cmd)
                    {
                        case LoadCommand or LoadBytesCommand:
                        {
                            await UnloadAndReset();

                            var (path, startAt, playing, playbackId) = cmd switch
                            {
                                LoadCommand l => (l.Path, l.Position, l.Playing, l.PlaybackId),
                                LoadBytesCommand b => (b.DisplayPath, b.Position, b.Playing, b.PlaybackId),
                                _ => throw new InvalidOperationException(),
                            };

                            try
                            {
                                reader = cmd is LoadBytesCommand bytes
                                             ? AudioReaderFactory.OpenBytes(bytes.AudioData)
                                             : AudioReaderFactory.Open(path);
                                var initialPosition = ApplySeek(reader, startAt);

                                // audio data path is:
                                // decoder -> Volume -> progress provider -> device
                                volumeProvider = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = volume };
                                trackEnded = BuildDevice(out device);
                                device!.Init(new ProgressProvider(volumeProvider.ToWaveProvider(), reader,
                                                                  position => ReportPosition(playbackId, position)));
                                MixerIdentity.TryApply();

                                PlaybackState committedState;
                                if (playing)
                                {
                                    device.Play();
                                    deviceStarted = true;
                                    committedState = PlaybackState.Playing;
                                }
                                else
                                    committedState = PlaybackState.Paused;

                                var updated = Update(current => new EngineSnapshot(
                                                         committedState, path, current.LastError, initialPosition,
                                                         DateTimeOffset.UtcNow, playbackId));
                                RaiseUpdated(updated);
                            }
                            catch (Exception e)
                            {
                                ReportError(e, "Load failed");
                                device?.Dispose();
                                device = null;
                                reader?.Dispose();
                                reader = null;
                                trackEnded = null;
                                volumeProvider = null;
                                deviceStarted = false;
                                var terminal = Update(current => new EngineSnapshot(
                                                          PlaybackState.Stopped, null, current.LastError, null,
                                                          DateTimeOffset.UtcNow, playbackId,
                                                          TerminalReason: EndReason.Failed));
                                RaiseUpdated(terminal);
                            }

                            break;
                        }

                        case ResumeCommand:
                            if (Snapshot.State == PlaybackState.Paused)
                            {
                                device?.Play();
                                deviceStarted = true;
                                var committed = Update(current => current with
                                {
                                    State = PlaybackState.Playing,
                                    ObservedAt = DateTimeOffset.UtcNow,
                                });
                                RaiseUpdated(committed);
                            }

                            break;

                        case PauseCommand:
                            if (Snapshot.State == PlaybackState.Playing)
                            {
                                device?.Pause();
                                var committed = Update(current => current with
                                {
                                    State = PlaybackState.Paused,
                                    ObservedAt = DateTimeOffset.UtcNow,
                                });
                                RaiseUpdated(committed);
                            }

                            break;

                        case StopCommand:
                            await UnloadAndReset();
                            var stopped = Update(current => current with
                            {
                                State = PlaybackState.Stopped,
                                Path = null,
                                Position = null,
                                ObservedAt = DateTimeOffset.UtcNow,
                                TerminalReason = null,
                            });
                            RaiseUpdated(stopped);
                            break;

                        case VolumeCommand v:
                            volume = Math.Clamp(v.Volume, 0f, 1f);
                            volumeProvider?.Volume = volume;
                            break;

                        case SeekCommand s:
                            if (reader is not null)
                            {
                                var position = ApplySeek(reader, s.Position);
                                Log.Debug("Seek -> {time}", reader.CurrentTime);
                                var committed = Update(current => current with
                                {
                                    Position = position,
                                    ObservedAt = DateTimeOffset.UtcNow,
                                });
                                RaiseUpdated(committed);
                            }

                            break;
                    }
                }
                catch (Exception e)
                {
                    ReportError(e, "Playback error");
                }
            }
        }
        catch (Exception e)
        {
            ReportError(e, "Music player crashed because of actor loop error");
        } finally
        {
            await UnloadAndReset();
        }

        return;

        async Task UnloadAndReset()
        {
            await Unload(device, reader, trackEnded, deviceStarted);
            device = null;
            reader = null;
            trackEnded = null;
            volumeProvider = null;
            deviceStarted = false;
        }
    }

    private TaskCompletionSource BuildDevice(out IWavePlayer? device)
    {
        device = deviceFactory();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.PlaybackStopped += (_, e) =>
        {
            Log.Debug("PlaybackStopped (ex={message})", e.Exception?.Message ?? "none");
            if (e.Exception is not null) ended.TrySetException(e.Exception);
            else ended.TrySetResult();
        };
        return ended;
    }

    private void ReportError(Exception e, string context)
    {
        Log.Error(e, "{context}", context);
        Update(current => current with
        {
            LastError = $"{context}: {e.Message}",
            ObservedAt = DateTimeOffset.UtcNow,
        });
    }

    public async ValueTask DisposeAsync()
    {
        mailbox.Writer.Complete();
        await loop;
    }
}
