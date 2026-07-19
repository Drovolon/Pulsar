namespace Pulsar.Ipc;

/// <summary>Emitted by BroadcastManager. Taken by IpcProvider and converted into API DTOs.</summary>
internal sealed record BroadcastPlayerData(
    string CurrentPath,
    string CurrentBlake3Hash,
    string CurrentSha1Hash,
    string PrefetchPath,
    string PrefetchBlake3Hash,
    string PrefetchSha1Hash,
    PulsarCursor Cursor);
