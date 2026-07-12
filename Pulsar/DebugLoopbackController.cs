using System.Threading;
using Pulsar.Ipc;

namespace Pulsar;

/// <summary>
/// Optional debug-only route from local broadcast output back into Pulsar's inbound
/// player-data path. UI code only toggles this flow; delivery is driven by the coordinator.
/// </summary>
internal sealed class DebugLoopbackController(IpcProvider ipc)
{
    private readonly Lock @lock = new();
    private bool enabled;

    internal void SetEnabled(bool value, (string, string[], PulsarCursor)? current)
    {
        lock (@lock)
        {
            if (enabled == value) return;
            enabled = value;
            ipc.ApplyDebugLoopback(value ? current : null);
        }
    }

    internal void OnPlayerDataChanged((string, string[], PulsarCursor)? data)
    {
        lock (@lock)
        {
            if (enabled) ipc.ApplyDebugLoopback(data);
        }
    }
}
