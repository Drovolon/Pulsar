# Pulsar.Api

Dependency-free submodule for Pulsar's IPC API.

TODO: this isn't really a submodule yet. But it will be made so before v1 is published.

Summary of methods:
```csharp
GetIpcSubscriber<PulsarApiVersion>(PulsarIpcEndpoints.ApiVersion);
GetIpcSubscriber<PulsarPlayerData?>(PulsarIpcEndpoints.GetPlayerData);
GetIpcSubscriber<ulong, string, string?, string, object?>(PulsarIpcEndpoints.SetPlayerData);
GetIpcSubscriber<ulong, object?>(PulsarIpcEndpoints.ClearPlayerData);
GetIpcSubscriber<PulsarPlayerData?, object?>(PulsarIpcEndpoints.PlayerDataChanged);
```
