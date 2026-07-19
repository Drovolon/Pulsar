using Dalamud.Configuration;
using Pulsar.Broadcast;
using System;
using System.Collections.Generic;
using Pulsar.Broadcast.Beefweb;

namespace Pulsar;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool DebugMode { get; set; } = false;
    public bool DebugLoopbackEnabled { get; set; } = false;

    // Broadcast (DJ) side: the local monitor/jukebox output volume.
    public float MonitorVolume { get; set; } = 1.0f;

    // Listening (receiver) side volumes.
    public float ListeningMasterVolume { get; set; } = 1.0f;
    // Map of character names -> volumes.
    public Dictionary<string, float> ListeningPairVolumes { get; set; } = [];
    // When nothing is explicitly pinned, auto-play whoever nearby is broadcasting.
    public bool ListeningAutoPlay { get; set; } = true;

    public bool MuteGameBgmWhileListening { get; set; } = true;
    public bool MuteGameBgmWhileBroadcasting { get; set; } = true;

    // Note: nearby-broadcaster only sends one notification, even if multiple conditions are met
    public bool NotifyNearbyBroadcaster { get; set; } = true;
    public bool NotifyNearbyBroadcasterAutoPlayOff { get; set; } = true;
    public bool NotifyNearbyBroadcasterWhileBroadcasting { get; set; } = false;
    public bool NotifyMutedPlayback { get; set; } = true;
    public bool NotifyListeningTrackChanged { get; set; } = false;
    public bool NotifyUnsyncableBroadcast { get; set; } = true;

    // Broadcast (DJ) side: which source the broadcast tab plays from.
    public BroadcastMode BroadcastMode { get; set; } = BroadcastMode.Folder;

    // Broadcast (DJ) side: persisted folders to broadcast from.
    public List<string> BroadcastFolders { get; set; } = [];

    // Broadcast (DJ) side: the directory *name* of the last-selected Penumbra mod
    public string? BroadcastMod { get; set; } = null;

    // Broadcast (DJ) side: whether the jukebox plays in shuffled order.
    public bool Shuffle { get; set; } = false;

    // Broadcast (DJ) side: beefweb source. Host isn't configurable, since we need to sync files...
    public int BeefwebPort { get; set; } = 8880;
    public string? BeefwebUsername { get; set; } = null;
    public string? BeefwebPassword { get; set; } = null;
    public BeefwebTransport BeefwebTransport { get; set; } = BeefwebTransport.Sse;

    // Re-check/prefetch the next track this many ms before the current one ends...
    public int PrefetchLeadMs { get; set; } = 30_000;
    // ...and a final re-check this many ms before the end.
    public int PrefetchFinalMs { get; set; } = 5_000;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
