using NAudio.Wave;
using PolyType;
using StreamJsonRpc;

namespace Pulsar.Common.Api;

public static class PipeNames
{
    /// <summary>Default if --pipe isn't provided (though in our case it currently always is)</summary>
    public const string AudioHost = "Pulsar.AudioHost";

    public const string AudioListening = "Pulsar.AudioHost.Listening";
    public const string AudioBroadcast = "Pulsar.AudioHost.Broadcast";

    public const string TranscodeHost = "Pulsar.TranscodeHost";
}

/// <summary>
/// API implemented by the AudioHost. Basically just a simple file player.
/// </summary>
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.AllPublic)]
public partial interface IRemoteEngine
{
    Task LoadAsync(
        string path, TimeSpan position, bool startPlaying, long playbackId, CancellationToken ct);
    Task LoadBytesAsync(
        string displayPath, byte[] audioData, TimeSpan position,
        bool startPlaying, long playbackId, CancellationToken ct);

    Task StopAsync(CancellationToken ct);
    Task PauseAsync(CancellationToken ct);
    Task ResumeAsync(CancellationToken ct);
    
    Task SetVolumeAsync(float volume, CancellationToken ct);
    
    Task SeekAsync(TimeSpan position, CancellationToken ct);
    
    Task<EngineSnapshot> GetStateAsync(CancellationToken ct);
    
    /// <summary>
    /// Fires when playback ends, whether naturally or because of an error. PlaybackId
    /// identifies the Load call that produced the event.
    /// </summary>
    event EventHandler<PlaybackEnded> OnPlaybackEnded;

    /// <summary>
    /// Fires on a load/track change, play, pause, resume, seek, and stop. Note, does NOT fire
    /// on a natural track end; that's what OnPlaybackEnded is for. OnChanged is more or less
    /// "the user took an action of some kind".
    ///
    /// Payload is the state after the event and identifies its originating Load.
    /// </summary>
    event EventHandler<EngineSnapshot> OnChanged;
}

[GenerateShape]
public partial record EngineSnapshot(
    PlaybackState State,
    string? Path,
    string? LastError,
    PlaybackPosition? Position,
    DateTimeOffset ObservedAt,
    long PlaybackId = 0);

[GenerateShape]
public partial record PlaybackEnded(long PlaybackId, EndReason Reason);

[GenerateShape]
public partial record PlaybackPosition(TimeSpan Current, TimeSpan Total);

public enum EndReason
{
    Finished, // the track played to its natural end
    Failed,   // a load or playback error tore it down
    Disconnected, // the audio host disappeared; the caller may restore playback after reconnect
}
