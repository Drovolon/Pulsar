using System;
using System.Threading.Tasks;
using Pulsar.Broadcast;

namespace Pulsar.Tests.Fakes;

public sealed class FakeMusicSource : IMusicSource
{
    public SourceSnapshot? Current { get; set; }
    public bool Disposed { get; private set; }

    /// <summary>When set, DisposeAsync parks on it - models a slow source teardown.</summary>
    public TaskCompletionSource? DisposeGate { get; set; }

    public event Action<SourceSnapshot?>? OnSnapshotChanged;
    public void RaiseChanged() => OnSnapshotChanged?.Invoke(Current);

    /// <summary>
    /// The subscriber list as a real event pump captures it just before invoking.
    /// Lets tests deliver an event that was already "in flight" when the manager
    /// unsubscribed - the mid-switch race a plain RaiseChanged can't reproduce.
    /// </summary>
    public Action<SourceSnapshot?>? CapturedHandlers => OnSnapshotChanged;

    public async ValueTask DisposeAsync()
    {
        if (DisposeGate is { } gate) await gate.Task;
        Disposed = true;
    }
}
