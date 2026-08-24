using System;
using System.Threading.Tasks;
using Pulsar.Broadcast;

namespace Pulsar.Tests.Fakes;

public sealed class FakeMusicSource : IMusicSource
{
    private SourceFrame? frame;

    public SourceSnapshot? Current
    {
        get => frame?.Snapshot;
        set => frame = value is null ? null : new SourceFrame(new SourceCursor(), value);
    }

    public SourceFrame? Frame => frame;
    public bool Disposed { get; private set; }

    /// <summary>When set, DisposeAsync parks on it - models a slow source teardown.</summary>
    public TaskCompletionSource? DisposeGate { get; set; }

    public event Action<SourceSnapshot?>? OnSnapshotChanged;
    public event Action? OnChanged;

    public void RaiseChanged()
    {
        OnSnapshotChanged?.Invoke(Current);
        OnChanged?.Invoke();
    }

    public void RaiseProjectionChanged(SourceSnapshot snapshot)
    {
        frame = frame is { } current
                    ? new SourceFrame(current.Cursor, snapshot)
                    : new SourceFrame(new SourceCursor(), snapshot);
        OnChanged?.Invoke();
    }

    /// <summary>
    /// The subscriber list as a real event pump captures it just before invoking.
    /// Lets tests deliver an event that was already "in flight" when the manager
    /// unsubscribed - the mid-switch race a plain RaiseChanged can't reproduce.
    /// </summary>
    public Action<SourceSnapshot?>? CapturedHandlers
    {
        get
        {
            var snapshots = OnSnapshotChanged;
            var changed = OnChanged;
            return snapshots is null && changed is null
                       ? null
                       : snapshot =>
                       {
                           snapshots?.Invoke(snapshot);
                           changed?.Invoke();
                       };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (DisposeGate is { } gate) await gate.Task;
        Disposed = true;
    }
}
