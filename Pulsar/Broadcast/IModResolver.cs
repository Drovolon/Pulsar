namespace Pulsar.Broadcast;

/// <summary>
/// What BroadcastManager needs from Penumbra: mod directory NAME -> directory on
/// disk (or null). Split out so LoadMod is testable without a Dalamud plugin interface.
/// </summary>
public interface IModResolver
{
    string? ResolveModDirectory(string modDirectoryName);
}
