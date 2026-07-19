namespace Pulsar.Api;

/// <summary>Major/minor API versions.</summary>
public readonly record struct PulsarApiVersion(int Major, int Minor);

/// <summary>Known Pulsar IPC API versions.</summary>
public static class PulsarApiVersions
{
    /// <summary>The version implemented by this API assembly.</summary>
    public static PulsarApiVersion Current => new(0, 1);
}
