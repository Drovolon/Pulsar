using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Pulsar.Common.Api;

namespace Pulsar.Tests.Fakes;

/// <summary>
/// IPrepareService whose per-file completion the test controls. Ungated files
/// "transcode" instantly; gated files block until the test opens (or fails) the
/// gate - honoring cancellation, like the real RPC call would.
/// </summary>
public sealed class ControllablePrepareService : IPrepareService
{
    public const string Blake3Hash =
        "AF1349B9F5F9A1A6A0404DEA36DCC9499BCB25C9ADC112B7CC9A93CAE41F3262";
    public const string Sha1Hash = "DA39A3EE5E6B4B0D3255BFEF95601890AFD80709";
    public sealed class Gate
    {
        internal readonly TaskCompletionSource<Exception?> Tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => Tcs.TrySetResult(null);
        public void Fail(Exception ex) => Tcs.TrySetResult(ex);
    }

    private readonly Lock @lock = new();
    private readonly Dictionary<string, Gate> gates = [];
    private readonly List<string> prepared = [];

    public double GainDb { get; set; } = -3.0;

    /// <summary>Size of the artifact written to the transcode-out path.</summary>
    public int ArtifactBytes { get; set; }

    /// <summary>Every originalPath PrepareAsync/PrepareBytesAsync was invoked for, in order.</summary>
    public IReadOnlyList<string> PrepareCalls
    {
        get { lock (@lock) return [.. prepared]; }
    }

    public Task<bool> WaitForPrepare(string originalPath, TimeSpan? timeout = null)
        => TestWait.Until(() => PrepareCalls.Contains(originalPath), timeout);

    /// <summary>Gate all future prepares of this path on an explicit Open()/Fail().</summary>
    public Gate GateFor(string originalPath)
    {
        lock (@lock)
        {
            if (!gates.TryGetValue(originalPath, out var gate))
                gates[originalPath] = gate = new Gate();
            return gate;
        }
    }

    /// <summary>Replace any existing (possibly completed) gate with a fresh one.</summary>
    public Gate Regate(string originalPath)
    {
        lock (@lock) return gates[originalPath] = new Gate();
    }

    public async Task<PreparedTrack> PrepareAsync(string originalPath, string transcodeOutPath, CancellationToken ct)
    {
        Gate? gate;
        lock (@lock)
        {
            prepared.Add(originalPath);
            gates.TryGetValue(originalPath, out gate);
        }

        if (gate is not null)
        {
            var failure = await gate.Tcs.Task.WaitAsync(ct);
            if (failure is not null) throw failure;
        }

        // The real host leaves the transcode on disk; caching decisions key on that.
        await System.IO.File.WriteAllBytesAsync(transcodeOutPath, new byte[ArtifactBytes], CancellationToken.None);
        return new PreparedTrack(transcodeOutPath, Blake3Hash, Sha1Hash, GainDb);
    }

    public Task<PreparedTrack> PrepareBytesAsync(string originalPath, byte[] audioData, string transcodeOutPath, CancellationToken ct)
        => PrepareAsync(originalPath, transcodeOutPath, ct);
}
