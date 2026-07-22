namespace Pulsar.Common.Api;

public abstract record PlaybackCommand;
public sealed record LoadCommand(string Path, TimeSpan Position, bool Playing, long PlaybackId = 0) : PlaybackCommand;
public sealed record LoadBytesCommand(
    string DisplayPath,
    byte[] AudioData,
    TimeSpan Position,
    bool Playing,
    long PlaybackId = 0) : PlaybackCommand;
public sealed record StopCommand : PlaybackCommand;
public sealed record PauseCommand : PlaybackCommand;
public sealed record ResumeCommand : PlaybackCommand;
public sealed record VolumeCommand(float Volume) : PlaybackCommand;
public sealed record SeekCommand(TimeSpan Position) : PlaybackCommand;
