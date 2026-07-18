using System;
using Dalamud.Game.Text.SeStringHandling;

namespace Pulsar;

/// <summary>Prints consistently formatted Pulsar messages to the in-game chat.</summary>
internal static class ChatNotifier
{
    // from `/xldata` -> UIColor
    private const ushort PulsarNameColor = 48; // purple
    private const ushort InformationColor = 504; // green
    private const ushort WarningColor = 31; // yellow

    public static void Information(string heading, string message) => Print(builder => builder
        .AddUiForeground(heading, InformationColor)
        .AddText(message));

    public static void Warning(string heading, string message) => Print(builder => builder
        .AddUiForeground(heading, WarningColor)
        .AddText(message));

    private static void Print(Action<SeStringBuilder> build)
    {
        _ = Plugin.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var builder = new SeStringBuilder().AddUiForeground("[Pulsar] ", PulsarNameColor);
                build(builder);
                Plugin.Chat.Print(builder.BuiltString);
            }
            catch (Exception e)
            {
                Plugin.Log.Error(e, "Failed to print Pulsar chat notification");
            }
        });
    }
}
