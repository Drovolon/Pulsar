using Xunit;

namespace Pulsar.Tests;

public class SmokeTest
{
    // Proves the test host runs at all on this box (net10.0-windows TFM under a Linux runtime,
    // Dalamud assemblies copy-local). Types from both referenced projects must load.
    [Fact]
    public void Referenced_project_types_load()
    {
        var config = new Configuration();
        Assert.Equal(Broadcast.BroadcastMode.Folder, config.BroadcastMode);

        var snapshot = new Common.Api.EngineSnapshot(
            NAudio.Wave.PlaybackState.Stopped, null, null, null, System.DateTimeOffset.UtcNow, 0);
        Assert.Null(snapshot.Path);
    }
}
