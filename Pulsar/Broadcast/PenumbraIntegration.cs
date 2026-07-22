using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Plugin;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;

namespace Pulsar.Broadcast;

public sealed record ModEntry(string DirectoryName, string Name);

/// <summary>
/// Used to power the "play a mod" feature in the Broadcast tab: enumerates mods and
/// resolves their top-level directories.
/// </summary>
public sealed class PenumbraIntegration : IModResolver, IDisposable
{
    private const int ExpectedBreaking = 5;

    private readonly ApiVersion apiVersion;
    private readonly GetModList getModList;
    private readonly GetModDirectory getModDirectory;
    private readonly EventSubscriber initialized;
    private readonly EventSubscriber disposed;

    private volatile bool available;

    public PenumbraIntegration(IDalamudPluginInterface pi)
    {
        apiVersion = new ApiVersion(pi);
        getModList = new GetModList(pi);
        getModDirectory = new GetModDirectory(pi);
        initialized = Initialized.Subscriber(pi, OnPenumbraInitialized);
        disposed = Disposed.Subscriber(pi, OnPenumbraDisposed);

        available = Probe();
    }

    /// <summary>True if Penumbra is installed, loaded, and on the expected breaking API version.</summary>
    public bool IsAvailable => available;

    private bool Probe()
    {
        try { return apiVersion.Invoke().Breaking == ExpectedBreaking; }
        catch { return false; }
    }

    private void OnPenumbraInitialized() => available = Probe();
    private void OnPenumbraDisposed() => available = false;

    /// <summary>
    /// All mods, sorted by display name. Empty if Penumbra is unavailable.
    /// Note: this can be a big list.
    /// </summary>
    public IReadOnlyList<ModEntry> GetMods()
    {
        try
        {
            // GetModList: directory name -> display name.
            return [.. getModList.Invoke()
                .Select(kv => new ModEntry(kv.Key, kv.Value))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Penumbra GetModList failed");
            return [];
        }
    }

    /// <summary>
    /// Returns the actual directory on disk for a mod, from its directory name.
    /// </summary>
    public string? ResolveModDirectory(string modDirectoryName)
    {
        if (string.IsNullOrEmpty(modDirectoryName))
        {
            Plugin.Log.Warning("No mod directory name provided");
            return null;
        }
        try
        {
            var root = getModDirectory.Invoke();
            if (string.IsNullOrEmpty(root))
            {
                Plugin.Log.Warning("Penumbra mod directory is null or empty");
                return null;
            }
            return ResolveUnder(root, modDirectoryName);
        }
        catch (Exception e)
        {
            Plugin.Log.Warning(e, "Penumbra GetModDirectory failed");
            return null;
        }
    }

    /// <summary>Resolves a mod path against the configured Penumbra root.</summary>
    internal static string? ResolveUnder(string root, string modDirectoryName)
    {
        var full = Path.GetFullPath(modDirectoryName, root);
        return Directory.Exists(full) ? full : null;
    }

    public void Dispose()
    {
        initialized.Dispose();
        disposed.Dispose();
    }
}
