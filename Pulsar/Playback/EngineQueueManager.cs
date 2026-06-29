using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Pulsar.Common.Api;

namespace Pulsar.Playback;

internal class EngineQueueManager : IAsyncDisposable
{
    private readonly IRemoteEngine player;
    private readonly Task commandLoop;
    
    private readonly Channel<PlaybackCommand> mailbox =
        Channel.CreateUnbounded<PlaybackCommand>(new UnboundedChannelOptions { SingleReader = true });

    internal EngineQueueManager(IRemoteEngine engine, CancellationToken ct)
    {
        player = engine;
        commandLoop = Task.Run(() => CommandLoop(ct), ct);
    }
    
    public async ValueTask DisposeAsync()
    {
        await commandLoop;
    }
    
    internal void Load(string path, TimeSpan position, bool startPlaying) 
        => Enqueue(new LoadCommand(path, position, startPlaying));
    internal void Stop() => Enqueue(new StopCommand());
    internal void Pause() => Enqueue(new PauseCommand());
    internal void Resume() => Enqueue(new ResumeCommand());

    // Since we're doing RPCs in Dispatch(), I'm mildly concerned that a GC stall
    // or general CPU pressure could mean we can't keep up with the incoming rate
    // volume/seek reqs, since those are sliders in the UI. So we'll keep track
    // of the last volume and seek position separately, and in Dispatch() read
    // directly from these fields for the Volume/Seek commands, rather than the
    // commands themselves. It's a bit of a hack, and it's definitely unnecessary
    // 99% of the time. But, this will be way better UX than the alternative.
    private volatile float latestVolume = -1f; // -1 == no volume ever set
    internal void Volume(float volume)
    {
        latestVolume = volume;
        Enqueue(new VolumeCommand(volume));
    }

    internal void ReapplyVolume()
    {
        if (latestVolume >= 0) Enqueue(new VolumeCommand(latestVolume));
    }

    private long latestPositionTicks;
    internal void Seek(TimeSpan position)
    {
        Interlocked.Exchange(ref latestPositionTicks, position.Ticks);
        Enqueue(new SeekCommand(position));
    }

    private void Enqueue(PlaybackCommand command) => mailbox.Writer.TryWrite(command);

    private async Task CommandLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var command = await mailbox.Reader.ReadAsync(ct);
                try
                {
                    await Dispatch(command, ct);
                }
                catch (Exception ex)
                {
                    // TODO: surface this to the user... somehow.
                    Plugin.Log.Error(ex, "Error while dispatching playback command {command}", command);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Unexpected error in EQM command loop");
            }
        }
    }

    private async Task Dispatch(PlaybackCommand command, CancellationToken ct)
    {
        var task = command switch
        {
            LoadCommand l => player.LoadFileAsync(l.Path, l.Position, l.Playing, ct),
            SeekCommand 
                => player.SeekAsync(TimeSpan.FromTicks(Interlocked.Read(ref latestPositionTicks)), ct),
            VolumeCommand => player.SetVolumeAsync(latestVolume, ct),
            PauseCommand => player.PauseAsync(ct),
            ResumeCommand => player.ResumeAsync(ct),
            StopCommand => player.StopAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };
        await task;
    }
}
