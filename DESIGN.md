# Pulsar Design

Despite the UI being simple, Pulsar has grown to be pretty complex under the hood.

## Goals & Non-Goals

These are a rough sketch of how I wanted the design to play out.

### Goals

* Independent audio output from game
* No reliance on Penumbra or game resources
* Support simple file selection (play folder)
* Basic Penumbra integration ("play folder" but against the Penumbra mod list)
* Support local (in-plugin) playback of .scd files
* Local media player API support, to allow using a program with a robust library system
* In-plugin broadcast should auto-advance to the next song (like a real media player)
* Allow multiple people to broadcast in one venue, with the listener choosing which to hear
* Seek listeners to the DJ's current position, including if the DJ seeks *while playing*
* Enforce ReplayGain in playback
* Transcode high-bitrate audio to be more reasonable for sync
* Have playback and transcode run as separate processes, to avoid crashing the game

### Non-Goals

* Implementing our own library manager - use foobar2000/deadbeef+beefweb
* Implementing audio file tag (ID3v2) support (again, use media player)
* Implementing our own file sync solution - let the sync platforms handle that
* Syncing any kind of DSP (equalizer, etc.) - sync is purely `(file, playback cursor)`
* Supporting audio streams instead of files
* Supporting anything except audio

## Ecosystem Position

The big TL;DR of how Pulsar slots into this ecosystem:

* User broadcasts some music from an IMusicSource (LocalPlaybackSource, Beefweb Watcher)
* Sync plugins get player data from either GetPlayerData() IPC or OnPlayerDataChanged() messages
* Player data has:
    1. the current active file that should be synced (& its BLAKE3 and SHA-1 hashes)
    2. one file (& hashes) to sync for prefetch, when we can figure out what plays next
    3. an *opaque* (to the sync plugin) blob that includes DJ playback position, track metadata like calculated
       loudness, original filename, artist & album & song title
* Sync plugin does the file syncing and whatnot
* On *a pair's machine* sync plugin calls SetPlayerData with:
    1. an address of the *source* player in the object table (just like other plugins whose data is synced)
    2. the listener-local active file path (which was synced by the plugin)
    3. the listener-local prefetch file path, or an empty string when there is none
    4. the sync-opaque data blob from the other client
* The listener's client will show it as an available listening source; depending on whether they're broadcasting
  themselves, whether they're listening to someone else, whether they have autoplay enabled, etc., it may or may not
  start automatically playing
* If the broadcaster leaves visibility, or logs off, etc., the sync plugin calls ClearPlayerData()
* On receiving the clear, Pulsar tears down that pair state, which may include stopping playback, switching to another
  source, etc.

## Architecture

Pulsar has the client (`Pulsar/`), which spawns three sub-processes when loaded:

1. Two audio hosts (`Pulsar.AudioHost/`), which each expose a JSON-RPC API over a named pipe to play actual audio on the
   user's PC. Uses NAudio with libsndfile or Windows Media Foundation for playback. One audio host is for listening (to
   others), one audio host is for monitoring the local broadcast (e.g. play from folder).
    * libsndfile supports .wav, .aiff, .flac, .ogg (vorbis), .opus/.ogg.opus, .mp3
    * WMF supports .aac, .m4a, .wma, .alac
    * We use Lumina to parse .scd, extract Vorbis bytes, and pass them over RPC to libsndfile
    * See AudioReaderFactory for more info
2. One transcode host (`Pulsar.TranscodeHost/`). Also exposes a JSON-RPC API. This is purely to run transcodes and
   loudness normalization out-of-process, to prevent native (unmanaged) exceptions from crashing the game.

Both host types use library code in `Pulsar.Common`.

The plugin supervises and restarts hosts gracefully. For example, while listening to a track, killing the
Pulsar.AudioHost.Listening process would result in a brief interruption in music playback. Notably, when the new process
is spawned, the plugin re-loads the previous track *where it left off*. So it *literally* sounds like playback skipping
some number of milliseconds.

### Actors

FilePlayer was one of the first classes written, though it now lives in the audio host. It's the "here's a local file
path, play it as sound" (like, real audio). It has complex local state and lifecycle semantics, like audio device
creation/destruction on file load / playback stop.

To manage all of that sanely (without sprinkling locks and semaphores everywhere), it uses the "actor" pattern: a task
loop that's awaiting new commands from a single-reader queue, and then applying them.

It turns out that's a perfect pattern for *so many* areas in the plugin. So, basically all features end up having one or
more "actors" awaiting commands. User actions like "next track" end up being messages thrown onto a queue. And so there
is now a `SerializedMailbox` class that wraps all of that up neatly, used all over the place.

I did some research into a proper actor framework, but they were all either incredibly heavyweight solutions (like
Akka.NET) or, for the lightweight ones, ended up buying very little. It turns out `async/await`, `Task.Run`, and a
`Channel` are hard to beat for simplicity.

## Plugin Arch

The plugin holds most of the complexity. There are a few major areas.

### Broadcast

Broadcast is for the DJ. The majority of that code lives in `Broadcast/`, with the most central class being
BroadcastManager.

Support `BroadcastMode`'s:

* Local folder playback - a `FolderTrackCatalogLoader` recursively finds audio files, and hands them to
  `LocalPlaybackSource`. It offers simple shuffle and next/prev controls. Intended to be lightweight, easy to use. Per
  non-goals, intentionally NOT a library manager. I didn't want to write code to manage ID3v2 tags, user-authored
  playlists, etc.
* Local mod playback - uses Penumbra IPC to offer a mod selector. When a mod is selected, it builds "groups" - the first
  is always "All Files" (equivalent of FolderTrackCatalogLoader). But it also reads `group_*.json` from the filesystem
  to *infer* "playlists" that users have built with DAM mods like Thunderdome. This is very best-effort. Anyway, that's
  all powered by `ModTrackCatalogLoader`.
* Beefweb - most complex, but allows the user to use any player that supports the beefweb API (at the time of writing:
  just foobar2000 and DeaDBeeF) to pick the song they want to play. This is implemented by the `Watcher` class in the
  Beefweb namespace.

BroadcastManager has a notion of the *selected provider*. Folder and mod playback share the Local provider (and its
queue); Beefweb is the other provider. Like everywhere, it has an actor loop that handles commands like "select
Beefweb", "go On Air", or "the source reported a change" (new song playing, for example).

Installing a provider, selecting it, and going On Air are deliberately separate operations. Loading a new Beefweb
watcher should not need a couple of boolean arguments that quietly say "also select this" or "also start broadcasting".
The caller asks for those things directly. This sounds like a small distinction, but it keeps source setup from growing
a bag of switches over time. Like a tumor.

All sources implement `IMusicSource`, which is all BroadcastManager actually uses: (1) have a way to get the current
source snapshot, (2) send an event when the source snapshot changes. The snapshot is "what are you playing, and where,
and when did you tell me that".

BroadcastManager also integrates with SyncPrep, which lives at `Broadcast/Prepare/`. SyncPrep:

1. transcodes high-bitrate tracks to a reasonable bitrate
2. does loudness calculation to apply ReplayGain during playback
    * note: target gain is put into the sync payload and applied during playback - it's not written as an RG tag in the
      file
3. calculates BLAKE3 and SHA-1 hashes so the sync plugins don't have to

(reminder, the actual *work* is done in the transcode host, out of the game process)

So, BroadcastManager has a so-called "transcode gap": a period of time when it announces the *previous* song while it
awaits prep finishing. It owns that whole handoff: which provider the DJ chose, whether On Air is enabled, what is
actually broadcasting, and when to retry. Providers just report what they see.

The UI gets one phase from BroadcastManager: Off Air, Starting, Live, Switching, Retrying, or Failed.

It also has an output queue where it publishes source changes. `ApplicationCoordinator` takes those changes and pushes
them into the IPC, debug loopback, listening, and BGM-muting layers.

#### Beefweb

The beefweb API requires some finesse. In essence, we want:

* what song is playing (file path)
* the cursor position (where in the song)
* change detection: user presses next/prev, pauses, seeks to a new position, etc.

That's all pretty easy *except* that the cursor is continually advancing as the song plays. We definitely do not want to
push an event to subscribers just because one second passed therefore the cursor is now one second later in the song.

So, our solution is:

* two event feeds: SSE and polling. SSE is the default, polling is an optional configurable fallback in case SSE is
  unreliable for some reason. in my testing, that appears to have been unnecessary - SSE works plenty fine for me.
* a Discriminator that watches new "observations" from the feeds and *classifies* them as genuinely novel or something
  that can be ignored
    * this is done by using monotonic timestamps and checking how far the cursor has changed since the last observation:
      e.g., if our last observation was 10 seconds ago, and the playback cursor has advanced by 10 seconds, the user has
      *not* performed a seek
* a `Watcher` that ties it all together

The Watcher reports Beefweb facts, including transitions into a source we cannot sync. It does not know whether Beefweb
is selected, whether the DJ is On Air, or whether warnings are enabled. BroadcastManager owns those decisions and is the
place that decides whether to put a warning in chat.

None of that machinery is necessary for the local playback source, since we are in the path of all user inputs.

### Listening

Listening is where the magic happens: where file paths (from synced pairs) become sound. It's also fairly complex. Like
broadcasts, there's a ListeningManager that coordinates the whole show.

The ListeningManager tracks pairs (a dict of ulong (address) to pair states). Only one pair can be listened to at a
time. It sends "desired" playback state in `EngineSession`. EngineSession manages `IRemoteEngine`: it assigns playback
IDs, handles stale events, polls for state, handles host disconnect/reconnect, etc.

The listening path operates on a "current and desired" model. "Desired" comes from the sync plugin (or from the debug
loopback path) - it's "I want this file to play, at this position, which was recorded at this timestamp". The
`SyncDecider` class takes the current and desired states as inputs, and outputs an `EngineAction` - what should be done
to make the confirmed engine snapshot match the desired. For example: if the desired is to pause, but that song is
currently playing, it would return `EngineAction.Pause`. ListeningManager sends *that* to `EngineSession`, which itself
has a "current and desired" model.

(It's a bit weird because EngineSession came later, when tightening up the broadcast path to support multiple catalogs.
So, there's an opportunity to refactor ListeningManager to be a thinner layer around EngineSession, probably.)

Like `BroadcastManager`, `ListeningManager` publishes changes through ApplicationCoordinator, which BgmMuter and
ListeningNotifier use.

#### Max Lag, Outro Grace Period

BroadcastManager outputs cursor position *and* the timestamp of that observation. Using this, we can calculate the DJ's
current cursor position (assuming no seeks). So it's possible to have listeners synchronized - everyone hearing the same
thing. In practice, it's a bit muddier:

* it takes time for files to sync - to transcode, get uploaded, payloads to get relayed by the sync client, and files to
  download.
* it would be bad UX to *begin* playing a song ~5-10 seconds into the start, just because it took 5-10 seconds to
  transcode/upload/relay/download.
* but it would be bad UX to *always* play a song from the beginning, because then the next track that plays could
  interrupt the current song, mid-song. a jarring experience

So, we have a MaxLag of 15 seconds - if the DJ position is less than 15 seconds into a song, then the song will start
playing *from the beginning* on the listening side. Likewise, if a new track comes in, and the current track has less
than 15 seconds remaining, we *let the current track finish* before starting the next one.

And when someone starts listening mid-song, they're started at the DJ position (minus a target lag of 5 seconds). This
lets the next song transition happen naturally.

Ultimately, this means Pulsar is "everyone hears the same thing - but possibly up to 15 seconds delayed".

## Debug Loopback

BroadcastManager ships updates to the ApplicationCoordinator, which forwards them to the IPC layer, the listening layer,
and the debug loopback layer.

Debug loopback is simple: take the output of BroadcastManager, like a sync plugin would, and forward it into
SetPlayerData for the listening layer. This exercises basically the whole plugin end-to-end, and is the easiest way to
test it.

`DebugLoopbackController` remembers the latest broadcast output even while loopback is disabled. Turning it on
republishes that value. So the UI only says "enabled" or "disabled"; it does not have to find a current broadcast
snapshot and pass it in at just the right time.

An example may be easiest, and it's probably illustrative of data flow:

* Broadcast tab: set the source to Beefweb, let's say
* Have a Beefweb player running
* Enable the On Air switch
* The Beefweb Watcher will notice when a song starts playing and notify the BroadcastManager
* The BroadcastManager will use SyncPrep to do any necessary transcode/loudness calculation
* When prep finishes, BroadcastManager will push the prepared file path, cursor position, and observation timestamp to
  ApplicationCoordinator
* ApplicationCoordinator informs the ListeningManager that broadcasting has begun
* The ListeningManager disables/ignores autoplay going forward, til broadcasting ends
* ApplicationCoordinator forwards that data to the DebugLoopbackController
* (If enabled) the DebugLoopbackController will call SetPlayerData() with that payload
* ListeningManager will show it as a "debug loopback" pair (address = ulong.MaxValue sentinel)
* In the Listening tab of the plugin, select the "debug loopback" pair (pinning it)
* ListeningManager notices the new pin and asks SyncDecider how to make the engine match the desired pair state
* (If it wasn't playing already) SyncDecider will likely return EngineAction.Load() with the file path and cursor
* ListeningManager will submit the load target to EngineSession
* EngineSession will invoke RemoteEngineLoad.LoadFileAsync
* RemoteEngineLoad will:
    * if it's an .scd file: use Dalamud's Lumina instance to parse the .scd file and extract the raw Vorbis bytes, then
      call IRemoteEngine.LoadBytesAsync()
    * if it's anything else: call IRemoteEngine.LoadAsync() with the raw file path
* IRemoteEngine is a StreamJsonRpc client connected to the NamedPipe for Pulsar.AudioHost.Listening - so the method call
  turns into a MessagePack'd RPC over a local pipe
* The audio host process receives the Load RPC (in the `AudioServer` class), which in turn passes the call through to
  `FilePlayer`
* FilePlayer unloads and resets the audio device, then builds a new one (this could be optimized)
* FilePlayer loads the file (or builds a bytestream) via `AudioReaderFactory`
* FilePlayer hands NAudio the loaded `WaveStream` and tells it to play audio

This same path applies for syncs, too, except the source of the pair data isn't the debug loopback coordinator - it's
the sync plugin itself, calling SetPlayerData() IPC.

## Audio Libraries

We use NAudio. It's a high quality sound library. It supports libsndfile (which we ship as a .dll) for cross-platform
playback of *most* formats. We fall back to Windows Media Foundation (WMF) for some specific codecs unsupported by
libsndfile.

This was an area of in-depth research during initial design. I ultimately made these choices:

* Managed audio libraries like Concentus (for Opus) have crash safety and security benefits, but appear to be much less
  maintained than the native, unmanaged libraries like libopus and libsndfile.
* Since managed code was out, I decided to separate the audio processing code from the game process through the host
  subprocesses. This added a huge amount of complexity, but in return the game is insulated from unmanaged exceptions
  like access violations. Even in my initial testing - before the subprocess split - I had an in-process transcode kill
  the game once. (Not reproducible. Not sure what happened. But it definitely killed it.)
* ffmpeg is the gold standard for format support, but it doesn't have an easy-to-use API wrapper available. Simple
  solutions like using ffmpeg for decoding to raw PCM had severe complications around seeking in particular. I felt it
  was far simpler to use NAudio + libsndfile + WMF as a fallback than try to shoehorn ffmpeg into the picture.

Setting aside decoding and playback, we also ship libebur128, which implements the EBU R 128 loudness normalization
standard. We don't *quite* implement ReplayGain, in the sense that we don't write gain values as tags - but we do follow
the rest of RG (-18 LUFS loudness target).
