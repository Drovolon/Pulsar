# IPC

I tried to keep this as simple as possible, closely mirroring existing plugins in this ecosystem.

## Reference

### Methods

* `bool IsEnabled()` - whether Pulsar IPC is enabled/ready
* `(int, int) ApiVersion()` - major and minor version of the API (currently `(0, 1)`)
* `(string, string, string)? GetPlayerData()`
  * first return value is the *active* file path, what the player is currently broadcasting. this file should be synced by the sync plugin
  * second return value is an optional *prefetch* file. this isn't always available. will be an empty string if unavailable. this should be synced by the sync plugin too (optionally)
  * third return value is an **opaque** (to the sync) payload. don't touch anything in here, sync it as-is.
* `void SetPlayerData(ulong, string, string, string)`
  * the mirror of GetPlayerData()
  * 1st argument is the *address* of the *source* player in the object table (it's a bit weird, I realize, but I wanted to mirror what's done for e.g. SimpleHeels)
  * 2nd argument is the *local file path* of the song to play. this should be the *synced* version of the 1st return value of GetPlayerData()
  * 3rd argument is the *local file path* of the prefetched song, or an empty string.
  * 4th argument is the **opaque** payload (the 3rd return value of GetPlayerData())
* `void ClearPlayerData(ulong)` - clear/stop playing for that player address

### Messages

* `void PlayerDataChanged(string, string, string)` - same as GetPlayerData(), but pushed as a message when broadcasting data changes
* `void Ready()` - fired when the plugin is ready for IPC
* `void Disposing()` - fired when the plugin is tearing down / being disposed

## Intended Flow

This *shouldn't* be too difficult to sync:

1. in the PlayerDataFactory, call `activePath, prefetchPath, payload = GetPlayerData()` to get the files to sync + the opaque payload
2. upload the two file paths returned by GetPlayerData()
3. ship the opaque payload as-is
4. on the receiving client side, when applying a character data payload: download the two file paths
5. call `SetPlayerData(addr, activePath, prefetchPath, payload)`

When the client leaves visibility, is paused, etc., call `ClearPlayerData(addr)`.

And of course, subscribe to the `PlayerDataChanged`, `Ready`, and `Disposing` messages.

File names don't need to be preserved. In the Lightless case, our `CharacterData` DTO got a new `public PulsarData? PulsarData` with the shape:

```csharp
[MessagePackObject(keyAsPropertyName: true)]
public sealed class PulsarData
{
    public string CurrentHash { get; set; } = string.Empty;
    public string PrefetchHash { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty; // opaque, relayed verbatim
}
```
