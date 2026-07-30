# Soundboard

Soundboard is a local Windows desktop application that combines a physical
microphone with one-shot sound effects and sends the mix to a VB-CABLE virtual
audio endpoint for Discord or a game.

## Current functionality

- discovers active Windows capture and render endpoints;
- retains selections by Windows Core Audio endpoint ID;
- captures the physical microphone through WASAPI shared mode;
- mixes the live microphone with one WAV, MP3, Ogg Opus, or Ogg Vorbis sound
  at a time;
- optionally sends that sound alone to physical headphones or speakers through
  a separate WASAPI shared-mode output;
- provides microphone volume, mute, peak metering, master sound volume, and a
  final-output meter;
- provides independent monitor volume and monitor-output peak metering;
- imports multiple audio files into a persistent local library;
- supports built-in library views, user categories, favorites, preset tile
  accents, editing, search, and persistent manual ordering;
- supports non-destructive trim-start, trim-end, fade-in, and fade-out playback
  settings with a decoded waveform editor;
- supports optional per-sound loudness normalization toward a configurable
  global target, disabled by default;
- applies configurable bounded-lookahead sample-peak safety limiting to the
  final virtual mix, sound-only monitor, and local preview;
- provides local-only edited-clip preview through a safe physical monitor
  endpoint;
- supports drag-and-drop import, handle-based tile reordering, keyboard
  reordering, tile playback, and remove;
- supports one optional persistent Windows global hotkey per sound plus an
  optional Stop Sound hotkey;
- detects duplicate content with a SHA-256 hash; and
- exposes engine, endpoint, mixer-format, overflow, playback, error, and
  library-path diagnostics.

The virtual-microphone output refuses physical speakers and headsets. The
optional monitor output does the inverse: it accepts only active physical
render endpoints and refuses likely VB-CABLE endpoints, with no override.
These restrictions keep microphone audio away from physical playback devices.

## Technology

- C# with nullable reference types and implicit usings
- .NET 10 WPF targeting `net10.0-windows`
- x64 Windows
- NAudio 2.3.0, NVorbis 0.10.5, Concentus.Oggfile 1.0.7, and Windows Core
  Audio APIs
- SDK-style projects and Git

All decoder packages are pinned to exact stable versions. WAV and MP3 retain
the existing NAudio path. NVorbis provides managed Ogg Vorbis decoding, and
Concentus.Oggfile plus its managed Concentus dependency provide Ogg Opus
decoding. Soundboard does not require FFmpeg, VLC, an external converter, a
runtime codec download, or network access.

## Supported audio formats

Soundboard accepts:

- `.wav`;
- `.mp3`;
- `.ogg` containing Opus audio;
- `.ogg` containing Vorbis audio; and
- `.opus` containing Opus audio in an Ogg container.

Ogg is a container, not an audio codec. The `.ogg` extension alone therefore
does not establish whether a file is playable. Soundboard verifies the Ogg
page structure and checksums, reads the identification packet, recognizes
`OpusHead` or the Vorbis identification signature, and routes the stream to
the matching managed decoder. This supports Discord-downloaded Ogg Opus files
when they are genuine Ogg containers. A Vorbis stream named `.opus` is also
reported and decoded as Vorbis based on its content.

An `.ogg` file containing video, multiple logical streams, an unsupported Ogg
codec, malformed headers, invalid checksums, unsupported multichannel audio,
or non-Ogg bytes is rejected. Soundboard does not claim support for every file
with an `.ogg` extension.

## Local data and privacy

Soundboard works entirely locally. It has no account, backend, cloud service,
database, telemetry integration, or Discord API integration.

Runtime data is stored under the current user's local application-data folder:

```text
%LOCALAPPDATA%\Soundboard\
├── library.json
├── settings.json
├── Waveforms\
│   └── <content-hash>-v<data-version>-b<bin-count>.json
├── Analysis\
│   └── <analysis-key>.json
└── Sounds\
    ├── <generated-id>.wav
    ├── <generated-id>.mp3
    ├── <generated-id>.ogg
    └── <generated-id>.opus
```

`library.json` uses schema version 6. It stores user categories with a stable
GUID, display name, normalized sort order, and UTC creation date. Sound records
contain a stable GUID, display name, managed filename, original filename,
detected container and codec, original extension, duration, UTC import date,
normalized manual sort order, SHA-256 content hash, optional hotkey, optional
category GUID, favorite state, and a controlled tile-accent preset. Each sound
also stores integer-millisecond trim start, optional trim end, fade-in
duration, and fade-out duration. A null trim end unambiguously means the full
original decoded duration. Each sound also stores whether loudness
normalization is enabled; cached measured loudness is derived data and is not
stored in the library document.
`settings.json` stores the global-hotkey enabled state and optional Stop Sound
hotkey alongside the existing audio and window settings. It also stores the
global normalization target, safety-limiter enabled state, and limiter
ceiling. Defaults are `-16 LUFS`, enabled, and `-1.0 dBFS`. JSON saves use a
temporary file and atomic replacement. A malformed library file is preserved
as `library.malformed-<timestamp>.json` before Soundboard creates an empty
library.

Version 1 through version 5 libraries migrate to schema version 6 during
startup.
Their existing sound sequence, stable IDs, names, managed filenames, original
filenames, original detected durations, content hashes, and hotkeys are
retained. Existing sounds default to full-duration playback with no fades. New
organization metadata defaults to Uncategorized, not favorite, and the
Default tile accent. Existing WAV and MP3 entries receive inferred container,
codec, and original-extension values. Invalid detected-format metadata falls
back safely with a concise startup warning instead of dropping an otherwise
valid sound. Invalid clip metadata is reset to full-duration playback with no
fades, and a concise startup warning is shown without dropping the sound or
touching its audio. Sound and category sort orders are normalized to unique
consecutive values while preserving the established sequence. The migrated
document is then saved with the same atomic replacement used by normal library
mutations. A future schema version is loaded conservatively and is never
silently rewritten as version 6. Existing and newly imported sounds default to
loudness normalization disabled.

Importing copies audio into `Sounds` with a generated filename. Soundboard
never modifies or deletes the original source file. Removing a tile deletes
only its managed copy. Clip editing never modifies or re-encodes either file,
never creates an edited audio copy, and never changes the content hash used
for duplicate detection.

`Waveforms` contains only bounded peak-amplitude data derived from decoded PCM,
not decoded audio. The cache is rebuildable and is never required for
playback. A missing or corrupt cache entry is regenerated when the clip editor
is opened. Cache writes are atomic, waveform generation does not rewrite
`library.json`, and startup maintenance removes orphaned cache entries where
possible. A cache failure is shown as a retryable warning while normal
playback remains available.

`Analysis` contains only versioned loudness measurements and their trim/fade
cache keys. It never contains decoded audio. The key includes the source
content hash, trim start/end, fade-in/out, and analysis algorithm version, so
editing a clip selects a different result automatically. Writes are atomic,
simultaneous identical requests are deduplicated, corrupt entries are ignored
and regenerated, and orphaned entries are removed where practical. Analysis
cache activity never rewrites `library.json` and is never needed when
normalization is disabled.

### Backup and complete removal

To back up the soundboard, close the app and copy the complete
`%LOCALAPPDATA%\Soundboard` folder. Restore the complete folder while the app
is closed so metadata and managed audio stay together.

To remove all local Soundboard data, close the app and delete
`%LOCALAPPDATA%\Soundboard`. The next launch starts with an empty library and
default settings. Original source audio elsewhere on the computer is
unaffected.

## VB-CABLE prerequisite

Install VB-CABLE separately and complete any restart requested by its installer
before using the audio engine. Soundboard does not install or configure an
audio driver.

VB-CABLE names its two sides from the perspective of normal audio
applications:

- `CABLE Input` is a render endpoint. Soundboard writes its microphone and
  sound mix here.
- `CABLE Output` is the related capture endpoint. Discord or a game selects
  this as its microphone.

```text
Physical microphone ─┐
                     ├─ Soundboard mixer → CABLE Input
One sound effect ────┘

One sound effect ────── Sound-only monitor mixer → Physical headphones

CABLE Output → Discord or game microphone
Discord or game output → Physical headphones
```

Friendly names can vary. Soundboard stores endpoint IDs and will not start
unless it finds both a likely VB-CABLE render endpoint and a related active
VB-CABLE capture endpoint.

## Restore, build, and run

From the repository root:

```powershell
dotnet restore
dotnet build --configuration Release
dotnet run --project .\src\Soundboard.App\Soundboard.App.csproj --configuration Release
```

## Start the audio engine

1. Select the physical microphone.
2. Select the standard VB-CABLE virtual render endpoint, normally
   `CABLE Input (VB-Audio Virtual Cable)`.
3. Optionally enable **Monitor sounds through headphones** and select a
   physical monitor output, such as
   `Speakers (2- Razer BlackShark V2 Pro)`.
4. Select **Start audio engine**.
5. Speak and verify the microphone and final-output meters move. The monitor
   meter must remain idle because microphone audio is never monitored.
6. Import sounds and select a sound tile.
7. Select **Stop audio engine** before changing devices, changing the monitor
   enable state, or refreshing devices.

Soundboard restores available saved endpoints. If the saved microphone is
unavailable, it uses the current default microphone. If the saved output is
unavailable, it prefers the standard `CABLE Input` render endpoint. It also
restores microphone volume, microphone mute, sound volume, and practical
window bounds. Monitoring enable state, the monitor endpoint ID, and monitor
volume are also restored. If the saved monitor endpoint is unavailable or is
now identified as virtual, Soundboard falls back to the current default active
non-virtual render endpoint, then another active physical render endpoint. The
audio engine never starts automatically.

Soundboard does not change Windows default recording or playback devices.

## Sound-only headphone monitoring

Monitoring is disabled by default on a new installation. When enabled,
Soundboard opens the selected physical render endpoint in WASAPI shared mode
when the engine starts. It sends only the current soundboard clip to that
device:

```text
To Discord:
Microphone + Soundboard → CABLE Input

To your headphones:
Soundboard only → Physical headset or speakers
```

Microphone monitoring is intentionally unsupported. The monitor mixer has no
microphone input, loopback capture, system audio, Discord audio, game audio, or
VB-CABLE capture input. Soundboard never enables Windows **Listen to this
device**.

The selected virtual and monitor endpoints can have different sample rates and
channel counts. Each sound gets a separate decoded source and is normalized
independently to a mono or stereo 32-bit floating-point mixer target derived
from that endpoint's own mix format. A monitor endpoint exposing more than two
channels is rejected with a clear warning rather than reinterpreted.

Monitor volume ranges from 0% to 200% and affects only local monitoring. Sound
volume affects the copy sent to Discord or a game. Setting monitor volume to
0% therefore silences local sound playback without silencing the virtual
microphone path.

The monitor enable setting, monitor selector, and device refresh are locked
while the engine runs; monitor volume remains adjustable. Settings changes
never restart the engine automatically. Soundboard never changes the Windows
default render device.

If the physical monitor is missing, disconnected, unsupported, or fails to
initialize or play, monitoring is disabled for that engine session and a
warning is shown. The primary microphone-plus-sound path remains running when
technically possible. Stop the engine, select another physical output, and
start it again. Soundboard does not silently switch outputs while running.

## Organize and manage sounds

Use **Import sounds** to select multiple `.wav`, `.mp3`, `.ogg`, and `.opus`
files, or drag files onto the soundboard area. The picker and drag-and-drop
path use the same decoder factory as playback. Every candidate is opened,
inspected by content, decoded far enough to prove it has readable audio,
checked for a valid duration, hashed with streaming SHA-256, and copied with
streaming I/O into the managed library. The original bytes and sensible
original extension are preserved; files are never converted to WAV or MP3.
Zero-byte, corrupt, unsupported, unreadable, over-1-GiB, and over-12-hour files
are skipped without blocking valid files. New imports are Uncategorized, not
favorite, use the Default tile accent, and appear at the end of the global
manual order. File drops onto the general soundboard area use the same
defaults. Category-targeted file drop is not currently implemented; assign
the category through **Edit** after import.

If the same content is imported again, Soundboard does not create another
managed copy. The summary identifies the existing sound by display name.
Duplicate detection is based on the exact original source bytes, not the
source path, filename, extension, or audible similarity. Reimporting the same
Discord `.ogg` is skipped, while a separately encoded MP3 with similar audio
is a different file.

The left library panel contains three fixed, non-deletable views:

- **All Sounds** shows the complete library in global manual order.
- **Favorites** shows favorite sounds without changing their global positions.
- **Uncategorized** shows sounds with no category assignment.

User categories are a single flat level below those built-in views. Category
names are trimmed, limited to 60 characters, and unique
case-insensitively. Use **Create category**, **Rename**, and **Delete** in the
library panel. The **Move up** and **Move down** buttons reorder user
categories; built-in views remain fixed. Renaming a category retains its
position. Deleting asks for confirmation, moves its sounds to Uncategorized
without changing their relative order, and never deletes their managed audio
files.

Each tile has a star control with an accessible **Add to favorites** or
**Remove from favorites** name. Favorite changes persist immediately and the
Favorites view refreshes immediately. A filtered-out sound retains its global
hotkey and remains playable through that hotkey while the app and audio engine
are running.

Use **Edit** to change a sound's trimmed display name, category, favorite
state, and one of the controlled Default, Blue, Purple, Green, Orange, Red,
Pink, or Teal accent presets in one atomic metadata update. The dialog also
shows the original filename, duration, concise detected format (`WAV`, `MP3`,
`OGG · Opus`, or `OGG · Vorbis`), and assigned hotkey without editing them.
Tiles show the same format label. Hotkeys remain in the dedicated hotkey
dialog. Editing does not rename the managed audio file and does not stop a
playing sound; its stable sound ID and visible Playing state remain attached
to the same tile.

### Edit a clip non-destructively

Use **Edit clip** on a sound tile to view the entire original decoded
waveform, original filename, detected format, original duration, proposed trim
start and end, effective playable duration, and fade-in and fade-out lengths.
The blue waveform always represents the complete original source. Excluded
audio is dimmed, orange regions show fades, and the two vertical handles select
the playable region.

Drag either handle, or use the accessible adjustment buttons. In the waveform,
press **S** or **E** to select the start or end handle, then use **Left** or
**Right**. The normal increment is 100 ms; hold **Shift** for 10 ms or
**Ctrl** for one second. Separate keyboard-focusable buttons move both trim
handles and increase or decrease both fades. A playable clip is always at
least 100 ms. Fades cannot be negative, extend beyond the trimmed clip, or
overlap each other.

**Play Preview** uses the proposed unsaved values and plays once through the
currently selected physical monitor endpoint in Windows shared mode. Preview
has its own decoder and output session. It never enters the main virtual
mixer, never uses `CABLE Input`, never includes microphone, Discord, game, or
system audio, and does not change the active sound tile. Therefore preview is
never transmitted to Discord. When the main engine is running, preview uses
only the already selected safe physical endpoint and never stops or switches
the engine. When the engine is stopped and that selection is unavailable, it
may use the current default active non-virtual render endpoint. If no safe
physical endpoint exists, preview reports an error.

**Save** validates and atomically persists the proposed values while preserving
the stable sound ID, managed filename, source duration, content hash, hotkey,
category, favorite state, accent, and sort order. If that sound is playing,
Save stops it first and does not restart it. **Cancel** stops preview and
changes no metadata or main playback. **Reset** proposes full
original-duration playback with zero fades; it remains unsaved until Save is
selected. Tiles show effective duration and a textual **Trimmed** indicator
whenever trim or fade edits are active. The editor continues to show the
original duration.

### Loudness normalization and safety limiting

**Normalize loudness** is optional for each sound and is disabled by default.
The clip editor can analyze the proposed unsaved trim and fade values, shows
the measured integrated loudness and maximum decoded sample peak, and previews
the proposed normalization locally. Analysis uses a managed-code
BS.1770-style gated integrated loudness calculation with K-weighting,
approximately 400 ms blocks with 75% overlap, an absolute gate near
`-70 LUFS`, and a relative gate near `-10 LU`. This wording does not claim
formal EBU certification.

The global target defaults to `-16 LUFS` and can be set from `-24 LUFS` to
`-10 LUFS`. Requested gain is:

```text
target LUFS - measured integrated LUFS
```

Applied gain is limited to a maximum `+12 dB` boost and `-24 dB` attenuation.
The editor shows both values and warns when the clamp prevents reaching the
target. Silence, clips shorter than the useful 400 ms window, invalid samples,
missing files, decoder failures, and unsupported channel counts remain
non-normalizable. Saving with normalization enabled requires a valid analysis
whose key exactly matches the proposed trim and fades. Changing an edit marks
the displayed result stale; choose **Reanalyze** before saving. **Reset**
changes only trim and fades and does not silently toggle normalization.

Tile clicks and global hotkeys share the same trigger path. If a normalized
sound has no matching cached result, Soundboard analyzes it asynchronously and
starts it only if that trigger is still the newest accepted request. A newer
sound selection supersedes the older pending trigger. Analysis failure leaves
the microphone engine running and does not silently play the sound
unnormalized. Tiles show **Normalized** only for a valid matching result and
**Normalization needs analysis** otherwise.

The sound path order is:

```text
Decode → trim/fades → optional normalization gain
→ existing sound volume → mix → final safety limiter → meter → output
```

Normalization is applied identically before the virtual and monitor branches'
output conversion and affects sound audio only. Microphone audio is never
normalized. The final virtual limiter follows the microphone-plus-sound mixer;
the monitor and preview use separate limiter instances after their sound-only
paths. Microphone audio remains excluded from both local paths.

The safety processor is a bounded-lookahead **sample-peak limiter**, not a
true-peak limiter. It defaults to a `-1.0 dBFS` ceiling, uses exactly `5 ms`
of lookahead and an approximately `100 ms` exponential release, and therefore
adds exactly `5 ms` of processing latency while enabled. Its allowed ceiling
range is `-6.0 dBFS` to `-0.1 dBFS`. Disabling it bypasses gain limiting; it
does not open another output or decoder. Diagnostics show current and maximum
gain reduction for virtual and monitor output, preview gain reduction where
available, and rejected non-finite sample counts.

All normalization and limiting are performed during playback. Soundboard
never rewrites or re-encodes the managed or original audio, never creates a
normalized copy, never changes the content hash or duplicate detection, and
never changes Windows or Discord audio settings.

Search is a case-insensitive substring match against display name, original
filename, and category name. The selected library view is applied first,
search second, and persistent manual order last. The interface displays the
selected view, visible count, total count, and distinct empty messages for an
empty library, empty category, empty Favorites view, and search with no
matches.

In All Sounds, drag a tile's dedicated `↕` handle to change the global sound
sequence. In Uncategorized or a user category, dragging changes the relative
order of sounds in that view while preserving sounds outside it. Search must
be empty. Reordering is disabled in Favorites and while search is active, with
an explanation shown above the tiles. A cancelled drag or a drop outside a
tile changes nothing; only a completed valid drop is saved. Right-click a tile
or press Shift+F10 and choose **Move earlier** or **Move later** for the
keyboard-accessible alternative. Tile clicks continue to play sounds and tile
buttons remain independent from the drag handle.

Remove asks for confirmation and deletes metadata plus the managed copy; the
original imported file is never modified. If managed-file deletion fails,
Soundboard reports the failure and rolls the library back instead of claiming
success.

Missing or unreadable managed files are skipped during startup with a warning
that identifies the metadata and local storage path. Restore the complete
library from backup or repair the affected metadata while Soundboard is
closed.

## One-shot playback

Each explicit tile selection starts that sound from its configured trim start.
The sound plays once, stops at its configured trim end, and never loops or
automatically restarts. With default clip settings, this is the full original
decoded duration.

- Selecting the playing tile explicitly stops its current session and starts a
  new session from the configured trim start.
- Selecting another tile stops the current sound and starts the selected sound.
- Only one sound effect is active at a time.
- **Stop sound** stops the effect without stopping the microphone engine.
- Selecting a tile while the engine is stopped shows an instruction to start
  the engine; it never starts routing silently.
- Natural completion returns the tile to idle while the microphone continues.

Each session carries the stable library sound ID and a monotonically increasing
session ID. Mixer end-of-stream queues completion only once. Completion removes
the mixer input, disposes all decoder, packet-reader, and file resources, and
clears playback only when the completed session is still current. A stale
callback from a stopped or replaced session cannot alter a newer session.
Virtual output and optional monitoring open independent decoded sources within
one logical session and apply the same immutable clip and normalization
settings. Trimming, fades, and optional normalization occur before per-output
resampling/channel conversion and final volume, so microphone audio is
unaffected. The virtual final limiter processes the complete microphone-plus-
sound mix; the monitor limiter sees sound only. Monitoring does not open a
second decoder when it is disabled. The virtual branch remains the
authoritative completion owner at the edited trim end. Opus pre-skip and final
granule trimming are applied, and neither Opus nor Vorbis pads end-of-stream
with indefinite silence.

## Global soundboard hotkeys

Global hotkeys let a sound tile be triggered while Notepad, Discord, a game, or
another application has focus. Soundboard must remain running for its hotkeys
to be active. The audio engine must also be started manually before a sound
hotkey can play audio; a hotkey never starts the engine, opens devices, changes
Windows settings, or steals foreground focus.

Each sound has an **Assign hotkey** action and shows one of these explicit
states:

- assigned and registered;
- assigned but unavailable;
- assigned while global hotkeys are disabled; or
- not assigned.

The assignment dialog captures one proposed combination only while its capture
area has focus. Select **Save** to ask Windows to register it, **Clear hotkey**
to remove the assignment, or **Cancel** (or press Escape) to leave the previous
assignment unchanged. Renaming a sound preserves its stable ID and hotkey.
Removing a sound unregisters its binding before its managed metadata and audio
copy are removed.

The compact **Global hotkeys** section provides:

- **Enable global hotkeys**, which unregisters every binding when disabled but
  preserves the assignments in JSON for re-registration when enabled again;
- an optional **Stop current sound** hotkey, which stops only the sound effect
  and leaves the microphone engine and both configured outputs running;
- **Retry unavailable hotkeys**, which makes one explicit retry without
  continuously polling in the background; and
- assigned, registered, and unavailable state text plus registration counts.

Supported combinations use Ctrl, Alt, Shift, and/or the Windows key with
letters, numbers, numpad digits, arrows, navigation keys, Escape, Enter, Space,
Tab, Backspace, Delete, or F1–F12. These ordinary keys require at least one
modifier so Soundboard cannot claim normal typing keys. F13–F24 may be assigned
without a modifier for dedicated macro-key devices. Modifier-only and unknown
keys are rejected.

Soundboard rejects duplicates between sounds and the Stop Sound action before
asking Windows. Windows may also refuse a combination because it is reserved
or already owned by another application. A failed new assignment is not
persisted, and a failed replacement restores the previous working assignment
where Windows permits it. Persisted assignments that are unavailable during
startup remain assigned and can be retried later; their sound tiles still work
with the mouse.

Registrations use the Windows `RegisterHotKey` API with `MOD_NOREPEAT`.
Therefore only explicitly registered combinations produce callbacks, and
holding a combination does not continuously restart a sound. Soundboard uses
`UnregisterHotKey` when a binding is cleared, removed, disabled, replaced, or
closed.

Soundboard does not use a low-level keyboard hook, raw keyboard input, keyboard
polling, simulated keystrokes, or administrator privileges. It does not record
keypress history, log unassigned keys, capture text typed into other
applications, or send input to Discord or games. Hotkey capture occurs only
inside the visible assignment dialog while that dialog has focus.

## Configure Discord manually

Soundboard does not change Discord settings. In Discord **Voice & Video**:

- **Input device:** `CABLE Output (VB-Audio Virtual Cable)`
- **Output device:** the physical Razer headset
- **Krisp / noise suppression:** disabled

Krisp and similar voice processing can suppress sound effects even when they
reach VB-CABLE correctly. Keep Discord output on the physical headset; do not
route Discord output back to VB-CABLE.

## Troubleshooting

### Missing VB-CABLE endpoints

Both a render endpoint such as `CABLE Input` and a capture endpoint such as
`CABLE Output` must be active. Complete VB-CABLE installation and any required
Windows restart, then select **Refresh devices**. Soundboard neither simulates
a virtual endpoint nor installs a driver.

### Device in use or disconnected

Stop other applications that may hold the endpoint exclusively, then restart
the Soundboard engine. Device removal or stream failures are shown in the
status area. A monitor-only failure does not intentionally stop the virtual
microphone. Device refresh is available only while the engine is stopped.

### Monitor output is rejected

The monitor selector contains active non-virtual render endpoints. A saved
endpoint that appears to be VB-CABLE is rejected and replaced with a safe
physical fallback. The monitor output cannot match the virtual output and
there is no override for virtual monitor endpoints.

### Unsupported format

The library accepts WAV, MP3, Ogg Opus, and Ogg Vorbis sources. Renaming
non-Ogg data to `.ogg` does not make it valid. If an Ogg file is rejected,
confirm that it is an audio-only Ogg container with Opus or Vorbis, then
download or export it again if it is truncated or corrupt. WebM Opus, Ogg
video, multiple-stream Ogg, and other Ogg codecs are not supported. No
external converter is bundled.

The mixer supports mono or stereo microphone/file sources and mono or stereo
output. Multichannel Ogg input and multichannel output are rejected rather
than silently reinterpreted.

### Crackling or growing latency

The microphone bridge is bounded to 250 ms. On overflow it clears stale audio
and increments the diagnostic overflow count instead of allowing latency to
grow indefinitely. Persistent overflows indicate the machine or driver cannot
service the selected endpoints reliably at that time.

## Current limitations

- one microphone, one VB-CABLE render endpoint, and one optional physical
  sound-only monitor endpoint;
- one WAV, MP3, Ogg Opus, or Ogg Vorbis sound effect at a time;
- mono/stereo sources and mono/stereo output only;
- one optional flat category per sound; no nested folders or tags;
- preset tile accents only; no arbitrary color picker or custom tile images;
- category-targeted file drop is not implemented;
- no hotkey profiles, per-game profiles, multiple trim regions, silence
  detection, automatic trimming, destructive editing, edited-file export, or
  destructive normalization;
- waveform preview does not currently draw a live playback cursor;
- sample-peak limiting only; no oversampled true-peak limiting, compressor,
  multiband dynamics, gate, equalizer, automatic microphone gain, noise
  reduction, pitch shifting, or time stretching;
- no microphone or system-audio monitoring through physical headphones;
- no system-audio capture or Discord-output capture;
- no Discord API integration or automatic Discord configuration;
- no cloud sync, SQLite database, tray integration, startup task, installer,
  or automatic updates.

Soundboard does not enable “Listen to this device,” request administrator
rights, install or modify drivers, change Windows defaults, or change Discord
settings automatically. Sound-only monitoring has not been claimed as audibly
verified by automated build validation; perform the manual headset and Discord
checks on the target computer before relying on it. The same applies to audible
trim/fade quality and preview routing: automated tests verify provider
boundaries and reject virtual preview endpoints, but do not substitute for a
headset-and-Discord check on the target machine.
