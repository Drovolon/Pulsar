namespace Pulsar.Api;

/// <summary>Public Dalamud IPC endpoint labels exposed by Pulsar.</summary>
public static class PulsarIpcEndpoints
{
    /// <summary>Prefix shared by every Pulsar endpoint.</summary>
    public const string Prefix = "Pulsar.";

    /// <summary>Notification sent once Pulsar IPC is ready.</summary>
    public const string Ready = $"{Prefix}OnReady";
    /// <summary>Notification sent before Pulsar IPC is disposed.</summary>
    public const string Disposing = $"{Prefix}OnDisposing";
    /// <summary>Notification carrying changed or stopped player data.</summary>
    public const string PlayerDataChanged = $"{Prefix}OnPlayerDataChanged";
    /// <summary>Function reporting whether Pulsar IPC is ready.</summary>
    public const string IsEnabled = $"{Prefix}IsEnabled";
    /// <summary>Function returning the implemented API version.</summary>
    public const string ApiVersion = $"{Prefix}ApiVersion";
    /// <summary>Function returning the currently broadcast player data.</summary>
    public const string GetPlayerData = $"{Prefix}GetPlayerData";
    /// <summary>Action applying synced player data to a source player.</summary>
    public const string SetPlayerData = $"{Prefix}SetPlayerData";
    /// <summary>Action clearing player data for a source player address.</summary>
    public const string ClearPlayerData = $"{Prefix}ClearPlayerData";
}
