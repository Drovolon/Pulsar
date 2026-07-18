using System;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Common.Api;
using Pulsar.Concurrency;

namespace Pulsar.Playback;

internal class EngineQueueManager : IAsyncDisposable
{
    private abstract record Command;
    private sealed record Playback(PlaybackCommand Value) : Command;
    private sealed record ReapplyVolumeRequested : Command;

    private readonly IRemoteEngine player;
    private readonly SerializedMailbox<Command> commands;
    private readonly CancellationToken ct;
    // Owned by the serialized command handler.
    private float latestVolume = -1f;

    internal EngineQueueManager(IRemoteEngine engine, CancellationToken ct)
    {
        player = engine;
        this.ct = ct;
        commands = new SerializedMailbox<Command>(
            command => new ValueTask(Dispatch(command, ct)),
            OnCommandError);
    }
    
    public ValueTask DisposeAsync() => commands.DisposeAsync();
    
    internal void Load(string path, TimeSpan position, bool startPlaying) 
        => Enqueue(new LoadCommand(path, position, startPlaying));
    internal void Stop() => Enqueue(new StopCommand());
    internal void Pause() => Enqueue(new PauseCommand());
    internal void Resume() => Enqueue(new ResumeCommand());

    internal void Volume(float volume) => Enqueue(new VolumeCommand(volume));

    internal void ReapplyVolume() => commands.TryPost(new ReapplyVolumeRequested());

    internal void Seek(TimeSpan position) => Enqueue(new SeekCommand(position));

    private void Enqueue(PlaybackCommand command) => commands.TryPost(new Playback(command));

    private void OnCommandError(Exception ex, Command command)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested) return;
        // TODO: surface this to the user... somehow.
        Plugin.Log.Error(ex, "Error while dispatching playback command {command}", command);
    }

    private async Task Dispatch(Command command, CancellationToken ct)
    {
        var task = command switch
        {
            Playback(LoadCommand l) => player.LoadFileAsync(l.Path, l.Position, l.Playing, ct),
            Playback(SeekCommand s) => player.SeekAsync(s.Position, ct),
            Playback(VolumeCommand v) => SetVolume(v.Volume, ct),
            Playback(PauseCommand) => player.PauseAsync(ct),
            Playback(ResumeCommand) => player.ResumeAsync(ct),
            Playback(StopCommand) => player.StopAsync(ct),
            ReapplyVolumeRequested when latestVolume >= 0 => player.SetVolumeAsync(latestVolume, ct),
            ReapplyVolumeRequested => Task.CompletedTask,
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null)
        };
        await task;
    }

    private Task SetVolume(float value, CancellationToken ct)
    {
        latestVolume = value;
        return player.SetVolumeAsync(value, ct);
    }
}
