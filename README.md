# Pulsar

**NOTE: PULSAR IS AN UNRELEASED WORK IN PROGRESS.** Release ETA: mid to late July.

Pulsar is a music plugin for the Mare family of syncing services. To use Pulsar:

* Both the DJ **and** the listener must have Pulsar installed.
* Both must be using a sync service that supports Pulsar.
    * At the time of writing: none does.
    * At release: Lightless will.
    * We hope other syncs will integrate it, too. (That's why it's a separate plugin
      and not integrated directly in Lightless.)

## How it Works

Pulsar works very differently from the normal DJ setup folks use with Penumbra mods like Thunderdome.exe. Pulsar has its own playback output, totally independent of the game. While someone is broadcasting, they can move around freely: redraw their character, switch outfits, use whatever emotes they like. It's not tied to the game at all. Pulsar also automatically plays the next song when the current one finishes. See the broadcasting section for more details.

Pulsar is integrated directly with the sync plugin. Files sync just like Penumbra mods. The broadcaster's position in the song are part of the character data that is synced. This is why it requires a sync plugin with Pulsar integration.

## Listening

Use `/pulsar` to open the UI. The listening tab will show anyone around you broadcasting, and what they're broadcasting (song artist and title if possible, or the original filename for the song if not). By default, it picks one and plays it automatically. (Turning it to "Off" in the Listening tab stops that.) You can also choose to override auto-play and force it to listen to a specific person's music.

There are two volume controls, master (which applies to everyone) and per-pair (which only applies to that one character playing music). The two stack: 50% master volume and 50% volume on a pair means that pair will play at 25% effective volume. Volume controls (including the mute button) are stored in your local Pulsar config.

If someone is playing music you don't like, you can mute them or pause sound sync with them.

In-game volume has no effect on Pulsar. Its playback is totally independent of the game. Only the controls in /pulsar affect it.

### Song Position Quirk

Unlike what we had before, Pulsar maintains a "maximum lag" behind the DJ's actual position. So, on first loading into a venue, if a song is already playing, the listener will begin in the *middle* of the song - alongside everyone else who was already listening. Although the listener won't hear the *opening* of the song anymore, they will hear the ending instead of a jarring skip to the next song.

## Broadcasting

Use `/pulsar` to open the UI. Broadcasting offers a few options:

* **"Play a folder"**. That just gives you a list of all the sound files in that folder (and recursively, all child folders). This has extremely simple controls: pause/resume, next/previous, a basic shuffle and seek support. (Yes, seeks are synced - if you fast-forward or rewind in a song, listeners will hear it too.) There's a basic search bar but no tag support. I explicitly did not want to build a "library manager" nor a playback queue / playlists.
* **"Play files from a mod"**. This is secretly just "play a folder" under the hood, but it lets you pick the mod from a list rather than hunting down the folder in your Penumbra storage directory. Yes, .scd files are supported, so you can point Pulsar at an existing Thunderdome.exe mod (for example) and play songs you've already loaded into the mod.
* (Most complicated, but most flexible) **Broadcast from a local music player**. At the time of writing, only foobar2000 and DeaDBeeF are supported, and the "beefweb" API extension must be installed. This does **NOT** sync DSP effects like equalizers, crossfade, etc. The only thing synced is: (1) the file you're playing, (2) your position in the file. But this option lets you use a real music library, with real tag support, playlists, a playback queue, etc. It's the best option for true curated playlists.

As written above, none of this interacts with the game, nor does it use Penumbra. So, you're free to move around, use whatever emote you like, redraw and switch outfits, etc. If you really like the Thunderdome.exe (or any other DAM mod) effects, I recommend using the emote with no song selected at all, so it's silent; then play your music through Pulsar.

### Transcoding and ReplayGain

For kinder bandwidth usage, any file with >=256Kbps bitrate will be transcoded to Opus @ 192 Kbps. ABX tests from HydrogenAudio suggest Opus in the 128 Kbps - 160 Kbps range is audibly indistinguishable from lossless (transparent); but because there is some uncertainty, I stepped it up to 192 Kbps as overkill. Transcoded files land in the Pulsar plugin directory as a cache, capped at 1 GB total size. **Note**: this only applies to people *broadcasting*; listeners' files are stored in the sync service's cache directory as normal.

For a consistent listening experience, track-level ReplayGain is forcibly applied as part of the output gain stage.

## Other Docs

* [DESIGN.md](./DESIGN.md) - technical information
* [CONTRIBUTING.md](./CONTRIBUTING.md) - how to contribute (for developers)
* [API.md](./API.md) - IPC API docs and general sync integration guide

## About the Author

Pulsar was created by Drovolon, one of the developers of Lightless. (Discord: @drovolon. I don't really use other social media.)
