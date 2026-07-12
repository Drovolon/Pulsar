using System.Threading.Channels;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Pulsar.Common;
using Pulsar.Common.Api;
using Serilog;

namespace Pulsar.AudioHost.Playback;

internal sealed class ProgressProvider(IWaveProvider source, WaveStream clock, Action<PlaybackPosition> report) : IWaveProvider
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
/// Exposes:
/// 1. a LastError if something goes wrong,
/// 2. a State (Stopped/Playing/Paused) with NowPlaying derived from it,
/// 3. an OnChanged event for events like stop/start/seek
/// 4. and an OnPlaybackEnded event for when the track finishes (or fails) on its own.
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
        loop = Task.Run(ProcessAsync);
    }

    private static IWavePlayer DefaultDevice() => new WasapiPlayerBuilder().WithMmcssThreadPriority().Build();

    public void Load(string path, TimeSpan position, bool playing) => mailbox.Writer.TryWrite(new LoadCommand(path, position, playing));
    public void LoadBytes(string displayPath, byte[] audioData, TimeSpan position, bool playing) => mailbox.Writer.TryWrite(new LoadBytesCommand(displayPath, audioData, position, playing));
    public void Stop() => mailbox.Writer.TryWrite(new StopCommand());
    public void Pause() => mailbox.Writer.TryWrite(new PauseCommand());
    public void Resume() => mailbox.Writer.TryWrite(new ResumeCommand());
    public void Volume(float volume) => mailbox.Writer.TryWrite(new VolumeCommand(volume));
    public void Seek(TimeSpan position) => mailbox.Writer.TryWrite(new SeekCommand(position));

    public string? LastError { get; private set; }

    private volatile PlaybackState state = PlaybackState.Stopped;
    public PlaybackState State => state;

    public string? Path { get; private set; }

    private volatile PlaybackPosition? position;
    public PlaybackPosition? Position => position;

    // Fires when playback ends naturally or fails to load or has a playback error.
    // NOT fired on an explicit stop.
    public event EventHandler<EndReason>? OnPlaybackEnded;

    public event EventHandler<EngineSnapshot>? OnChanged;

    private void RaiseChanged()
    {
        var snapshot = new EngineSnapshot(state, Path, LastError, position, DateTimeOffset.UtcNow);
        try
        {
            OnChanged?.Invoke(this, snapshot);
        }
        catch (Exception e)
        {
            Log.Error(e, "OnChanged subscriber threw");
        }
    }

    private void ApplySeek(WaveStream reader, TimeSpan to)
    {
        reader.CurrentTime =
            to < TimeSpan.Zero ? TimeSpan.Zero
            : to > reader.TotalTime ? reader.TotalTime
            : to;
        position = new PlaybackPosition(reader.CurrentTime, reader.TotalTime);
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
        }
        finally
        {
            device?.Dispose();
            reader?.Dispose();
            position = null;
            Path = null;
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
                if (trackEnded is not null
                    && await Task.WhenAny(next, trackEnded.Task) == trackEnded.Task)
                {
                    var reason = trackEnded.Task.IsFaulted ? EndReason.Failed : EndReason.Finished;
                    Log.Debug("Playback ended ({reason})", reason);
                    await UnloadAndReset();
                    state = PlaybackState.Stopped;
                    OnPlaybackEnded?.Invoke(this, reason);
                    continue;
                }

                PlaybackCommand cmd;
                try { cmd = await next; }
                catch (ChannelClosedException) { break; }
                next = mailbox.Reader.ReadAsync().AsTask();

                if (cmd is not VolumeCommand) // these are way too chatty
                    Log.Debug("cmd={cmd}  trackEnded={ended}", cmd, trackEnded is null ? "null" : trackEnded.Task.Status.ToString());

                try
                {
                    switch (cmd)
                    {
                        case LoadCommand or LoadBytesCommand:
                        {
                            await UnloadAndReset();

                            var (path, startAt, playing) = cmd switch
                            {
                                LoadCommand l => (l.Path, l.Position, l.Playing),
                                LoadBytesCommand b => (b.DisplayPath, b.Position, b.Playing),
                                _ => throw new InvalidOperationException(),
                            };

                            try
                            {
                                reader = cmd is LoadBytesCommand bytes
                                    ? AudioReaderFactory.OpenBytes(bytes.AudioData)
                                    : AudioReaderFactory.Open(path);
                                ApplySeek(reader, startAt);
                                Path = path;

                                // audio data path is:
                                // decoder -> Volume -> progress provider -> device
                                volumeProvider = new VolumeSampleProvider(reader.ToSampleProvider()) { Volume = volume };
                                trackEnded = BuildDevice(out device);
                                device!.Init(new ProgressProvider(volumeProvider.ToWaveProvider(), reader, p => position = p));
                                MixerIdentity.TryApply();

                                if (playing)
                                {
                                    device.Play();
                                    deviceStarted = true;
                                    state = PlaybackState.Playing;
                                }
                                else
                                {
                                    state = PlaybackState.Paused;
                                }
                                RaiseChanged();
                            }
                            catch (Exception e)
                            {
                                ReportError(e, "Load failed");
                                device?.Dispose(); device = null;
                                reader?.Dispose(); reader = null;
                                trackEnded = null; volumeProvider = null; deviceStarted = false;
                                Path = null; position = null;
                                state = PlaybackState.Stopped;
                                OnPlaybackEnded?.Invoke(this, EndReason.Failed);
                            }

                            break;
                        }

                        case ResumeCommand:
                            if (state == PlaybackState.Paused)
                            {
                                device?.Play();
                                deviceStarted = true;
                                state = PlaybackState.Playing;
                                RaiseChanged();
                            }
                            break;

                        case PauseCommand:
                            if (state == PlaybackState.Playing)
                            {
                                device?.Pause();
                                state = PlaybackState.Paused;
                                RaiseChanged();
                            }
                            break;

                        case StopCommand:
                            await UnloadAndReset();
                            state = PlaybackState.Stopped;
                            RaiseChanged();
                            break;

                        case VolumeCommand v:
                            volume = Math.Clamp(v.Volume, 0f, 1f);
                            volumeProvider?.Volume = volume;
                            break;

                        case SeekCommand s:
                            if (reader is not null)
                            {
                                ApplySeek(reader, s.Position);
                                Log.Debug("Seek -> {time}", reader.CurrentTime);
                                RaiseChanged();
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
        }
        finally
        {
            await UnloadAndReset();
        }

        return;

        async Task UnloadAndReset()
        {
            await Unload(device, reader, trackEnded, deviceStarted);
            device = null; reader = null; trackEnded = null; volumeProvider = null; deviceStarted = false;
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
        LastError = $"{context}: {e.Message}";
    }

    public async ValueTask DisposeAsync()
    {
        mailbox.Writer.Complete();
        await loop;
    }
}
