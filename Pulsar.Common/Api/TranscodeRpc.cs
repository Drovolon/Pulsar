using NAudio.Wave;
using PolyType;
using StreamJsonRpc;

namespace Pulsar.Common.Api;

/// <summary>
/// Implemented by the TranscodeHost.
/// </summary>
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.AllPublic)]
public partial interface IPrepareService
{
    Task<PreparedTrack> PrepareAsync(string originalPath, string transcodeOutPath, CancellationToken ct);

    // Used for .scd files (where audio data is extracted plugin-side through Lumina)
    Task<PreparedTrack> PrepareBytesAsync(string originalPath, byte[] audioData, string transcodeOutPath, CancellationToken ct);
}

[GenerateShape]
public partial record PreparedTrack(string SyncPath, double GainDb);
