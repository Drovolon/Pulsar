# IPC

I tried to keep this as simple as possible, closely mirroring existing plugins in this ecosystem.

See [`Pulsar.Api`](./Pulsar.Api) submodule. Feel free to import that as a submodule or vendor it. It's dependency-free and declares types.

## Reference

### Methods

Note: take a look at [PulsarPlayerData.cs](./Pulsar.Api/PulsarPlayerData.cs).

* `bool IsEnabled()` - whether Pulsar IPC is enabled/ready
* `PulsarApiVersion ApiVersion()` - major and minor version of the API (currently `0.1`)
* `PulsarPlayerData? GetPlayerData()`
  * `Current` is the active `PulsarSyncFile`, containing the path to upload and the BLAKE3 + SHA-1 hashes of the file bytes
  * `Prefetch` is an optional upcoming `PulsarSyncFile` that may be uploaded ahead of time
  * `Payload` is **opaque** to the sync; don't touch anything in here, relay it as-is
  * BLAKE3 hashes are encoded as 64 uppercase hex characters; SHA-1 hashes as 40 uppercase hex characters
  * use whichever hash algorithm your service uses, or re-hash it yourself I suppose
* `void SetPlayerData(ulong, string, string?, string)`
  * the mirror of GetPlayerData()
  * 1st argument is the address of the source player in the object table
  * 2nd argument is the local path of the downloaded `Current` file
  * 3rd argument is the local path of the downloaded `Prefetch` file, or `null`
  * 4th argument is the opaque payload received from GetPlayerData()
* `void ClearPlayerData(ulong)` - clear/stop playing for that player address

### Messages

* `void PlayerDataChanged(PulsarPlayerData?)` - same as GetPlayerData(), but pushed as a message when broadcasting data changes; `null` means broadcasting stopped
* `void Ready()` - fired when the plugin is ready for IPC
* `void Disposing()` - fired when the plugin is tearing down / being disposed

## Intended Flow

This *shouldn't* be too difficult to sync:

1. in the PlayerDataFactory, call `var data = GetPlayerData()` to get the files to sync, their BLAKE3/SHA-1 hashes, and the opaque payload
2. upload `data.Current.Path` and optionally `data.Prefetch.Path`. see `data.{Current,Prefetch}.{Blake3Hash,Sha1Hash}` as needed.
3. ship `data.Payload` as-is, without modification
4. on the receiving client, if `data == null`, call `ClearPlayerData(addr)`
5. otherwise, download the files, then call `SetPlayerData(addr, currentPath, prefetchPath, payload)`

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
