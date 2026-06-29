using System;
using System.Threading.Channels;
using System.Threading.Tasks;
using NAudio.Wave;

namespace Pulsar.Playback;

public sealed record PlaybackPosition(TimeSpan Current, TimeSpan Total);

internal sealed class ProgressProvider(AudioFileReader source, Action<PlaybackPosition> report) : IWaveProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<byte> buffer)
    {
        var n = source.Read(buffer);
        report(new PlaybackPosition(source.CurrentTime, source.TotalTime));
        return n;
    }
}

/// <summary>
/// FilePlayer uses NAudio to actually *play* audio files. Like, audible to the user.
/// It's a pretty simple API: play a file path, stop playing, adjust volume, seek to a position.
/// Exposes a LastError if something goes wrong, a NowPlaying that will be true if audio is playing,
/// and offers an event for when the track finishes playing naturally.
/// </summary>
public sealed class FilePlayer : IAsyncDisposable
{
    private abstract record Command;
    private sealed record PlayCommand(string Path) : Command;
    private sealed record StopCommand : Command;
    private sealed record VolumeCommand(float Volume) : Command;
    private sealed record SeekCommand(TimeSpan Position) : Command;

    private readonly Channel<Command> mailbox = Channel.CreateUnbounded<Command>(new() { SingleReader = true });
    private readonly Task loop;

    public FilePlayer() => loop = Task.Run(ProcessAsync);

    public void Play(string path) => mailbox.Writer.TryWrite(new PlayCommand(path));
    public void Stop() => mailbox.Writer.TryWrite(new StopCommand());
    public void Volume(float volume) => mailbox.Writer.TryWrite(new VolumeCommand(volume));
    public void Seek(TimeSpan position) => mailbox.Writer.TryWrite(new SeekCommand(position));

    public string? LastError { get; private set; }
    public bool NowPlaying { get; private set; } = false;

    private volatile PlaybackPosition? position;
    public PlaybackPosition? Position => position;

    // Triggered when a track finishes on its own, not stopped or a new track started.
    public event Action? OnTrackFinished;

    private async Task Unload(IWavePlayer? device, AudioFileReader? reader, TaskCompletionSource? trackEnded)
    {
        Plugin.Log.Debug($"Unload enter (device={(device is null ? "null" : "present")})");
        if (device is null) return;
        try
        {
            device.Stop();
            Plugin.Log.Debug("Unload: Stop() returned, awaiting trackEnded");
            if (trackEnded is not null) await trackEnded.Task;
            Plugin.Log.Debug("Unload: trackEnded done, disposing");
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
        }
    }

    private async Task ProcessAsync()
    {
        IWavePlayer? device = null;
        AudioFileReader? reader = null;
        TaskCompletionSource? trackEnded = null;
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
                    Plugin.Log.Debug(">> NATURAL-END branch");
                    await Unload(device, reader, trackEnded);
                    device = null; reader = null; trackEnded = null;
                    OnTrackFinished?.Invoke();
                    continue;
                }

                Command cmd;
                try { cmd = await next; }
                catch (ChannelClosedException) { break; }
                next = mailbox.Reader.ReadAsync().AsTask();
                Plugin.Log.Debug($"cmd={cmd.GetType().Name}  trackEnded={(trackEnded is null ? "null" : trackEnded.Task.Status.ToString())}");

                try
                {
                    switch (cmd)
                    {
                        case PlayCommand p:
                            await Unload(device, reader, trackEnded);
                            device = null; reader = null; trackEnded = null;

                            reader = new AudioFileReader(p.Path) { Volume = volume };
                            trackEnded = BuildDevice(ref device);
                            device!.Init(new ProgressProvider(reader, p => position = p));
                            device.Play();
                            NowPlaying = true;

                            break;

                        case StopCommand:
                            await Unload(device, reader, trackEnded);
                            device = null; reader = null; trackEnded = null;
                            break;

                        case VolumeCommand v:
                            volume = Math.Clamp(v.Volume, 0f, 1f);
                            reader?.Volume = volume;
                            break;

                        case SeekCommand s:
                            reader?.CurrentTime =
                                s.Position < TimeSpan.Zero ? TimeSpan.Zero
                                : s.Position > reader.TotalTime ? reader.TotalTime
                                : s.Position;
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
            ReportError(e, "Actor loop error, music player dead");
        }
        finally
        {
            await Unload(device, reader, trackEnded);
        }
    }

    private TaskCompletionSource BuildDevice(ref IWavePlayer? device)
    {
        device = new WasapiPlayerBuilder().Build();
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.PlaybackStopped += (_, e) =>
        {
            Plugin.Log.Debug($"PlaybackStopped (ex={e.Exception?.Message ?? "none"})");
            NowPlaying = false;
            if (e.Exception is not null) ended.TrySetException(e.Exception);
            else ended.TrySetResult();
        };
        return ended;
    }

    private void ReportError(Exception e, string context)
    {
        Plugin.Log.Error(e, context);
        LastError = $"{context}: {e.Message}";
    }

    public async ValueTask DisposeAsync()
    {
        mailbox.Writer.Complete();
        await loop;
    }
}
