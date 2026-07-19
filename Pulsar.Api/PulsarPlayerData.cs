namespace Pulsar.Api;

/// <summary>A local file ready for a sync plugin to upload.</summary>
/// <param name="Path">Absolute path to the exact file that should be uploaded.</param>
/// <param name="Blake3Hash">BLAKE3 hash of the file bytes, as 64 uppercase hex characters.</param>
/// <param name="Sha1Hash">SHA-1 hash of the file bytes, as 40 uppercase hex characters.</param>
public sealed record PulsarSyncFile(string Path, string Blake3Hash, string Sha1Hash);

/// <summary>Player data published by Pulsar for a sync plugin to relay.</summary>
/// <param name="Current">The file currently being broadcast.</param>
/// <param name="Prefetch">An optional upcoming file that may be uploaded in advance.</param>
/// <param name="Payload">Opaque Pulsar state to relay unchanged.</param>
public sealed record PulsarPlayerData(
    PulsarSyncFile Current,
    PulsarSyncFile? Prefetch,
    string Payload);
