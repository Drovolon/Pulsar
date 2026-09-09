<p align="center">
  <img src="https://raw.githubusercontent.com/Drovolon/Pulsar/main/Pulsar/images/logo.png" width="256" height="256" />
</p>

# Pulsar

Pulsar is a music plugin for the Mare family of syncing services. To use Pulsar:

* Both the DJ **and** the listener must have Pulsar installed.
* Both must be using a sync service that supports Pulsar.
    * At the time of writing: Lightless does.
    * We hope other syncs will integrate it, too. (That's why it's a separate plugin and not integrated directly in
      Lightless.)

## Installation

**See [pulsar.drovolon.org](https://pulsar.drovolon.org) for a full guide**

Repo

```
https://raw.githubusercontent.com/Drovolon/Pulsar/repo/repo.json
```

## Other Docs

* [DESIGN.md](./DESIGN.md) - technical information
* [CONTRIBUTING.md](./CONTRIBUTING.md) - how to contribute (for developers)
* [API.md](./API.md) - IPC API docs and general sync integration guide

## About the Author

Pulsar was created by Drovolon, one of the developers of Lightless. (Discord: @drovolon)

# Technical

## Support

Join the [Magitek Industries Discord](https://discord.gg/aQPcTjzrYr). Get the Pulsar
role and ask in `#pulsar-support`.

## Reporting Bugs

Please open a GitHub Issue. Include reproduction steps and ideally Dalamud logs
at the debug level.

## Release Process

The dev build is pinned at 0.0.3, for reasons. Otherwise, to perform a release,
create a GitHub release with a *tag* like `vX.X.X.X`. Pre-releases will automatically
be deployed to testing; full releases go to stable. Release title doesn't matter.
Description goes into the in-game changelog for that release.

Promoting a pre-release to release *should* work too, but I haven't tested it.
