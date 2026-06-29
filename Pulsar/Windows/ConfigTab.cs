using Dalamud.Bindings.ImGui;

namespace Pulsar.Windows;

internal sealed class ConfigTab(Plugin plugin)
{
    public void Draw()
    {
        var debug = plugin.Configuration.DebugMode;
        if (ImGui.Checkbox("Debug Mode", ref debug))
        {
            plugin.Configuration.DebugMode = debug;
            plugin.Configuration.Save();
        }
    }
}
