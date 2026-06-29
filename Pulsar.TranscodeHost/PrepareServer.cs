using Pulsar.Common.Api;
using Pulsar.TranscodeHost.Prepare;

namespace Pulsar.TranscodeHost;

public class PrepareServer : IPrepareService
{
    public async Task<PreparedTrack> PrepareAsync(string originalPath, string transcodeOutPath, CancellationToken ct)
    {
        return await Task.Run(() => TrackProcessor.Process(originalPath, transcodeOutPath, ct), ct);
    }

    public async Task<PreparedTrack> PrepareBytesAsync(string originalPath, byte[] audioData, string transcodeOutPath, CancellationToken ct)
    {
        return await Task.Run(() => TrackProcessor.ProcessBytes(originalPath, audioData, transcodeOutPath, ct), ct);
    }
}
