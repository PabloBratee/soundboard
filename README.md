# Soundboard

Soundboard is a local Windows desktop application that combines a physical
microphone with one-shot sound effects and sends the mix to a VB-CABLE virtual
audio endpoint for Discord or a game.

## Current functionality

- discovers active Windows capture and render endpoints;
- retains selections by Windows Core Audio endpoint ID;
- captures the physical microphone through WASAPI shared mode;
- mixes the live microphone with one WAV or MP3 sound at a time;
- optionally sends that sound alone to physical headphones or speakers through
  a separate WASAPI shared-mode output;
- provides microphone volume, mute, peak metering, master sound volume, and a
  final-output meter;
- provides independent monitor volume and monitor-output peak metering;
- imports multiple audio files into a persistent local library;
- supports drag-and-drop import, tile playback, search, rename, and remove;
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
- NAudio 2.3.0 and Windows Core Audio APIs
- SDK-style projects and Git

NAudio is pinned to stable version 2.3.0.

## Local data and privacy

Soundboard works entirely locally. It has no account, backend, cloud service,
database, telemetry integration, or Discord API integration.

Runtime data is stored under the current user's local application-data folder:

```text
%LOCALAPPDATA%\Soundboard\
├── library.json
├── settings.json
└── Sounds\
    ├── <generated-id>.wav
    └── <generated-id>.mp3
```

`library.json` stores a schema version and sound records containing a stable
ID, display name, managed filename, original filename, WAV/MP3 type, duration,
UTC import date, sort order, and SHA-256 content hash. JSON saves use a
temporary file and atomic replacement. A malformed library file is preserved
as `library.malformed-<timestamp>.json` before Soundboard creates an empty
library.

Importing copies audio into `Sounds` with a generated filename. Soundboard
never modifies or deletes the original source file. Removing a tile deletes
only its managed copy.

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
channel counts. Each sound gets a separate reader and is normalized
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

## Import and manage sounds

Use **Import sounds** to select multiple `.wav` and `.mp3` files, or drag files
onto the soundboard area. Every candidate is opened through the audio-reading
stack, inspected for duration, hashed, and then copied into the managed
library. Invalid and unreadable files are skipped without blocking valid
files.

If the same content is imported again, Soundboard does not create another
managed copy. The summary identifies the existing sound by display name.
Duplicate detection is based on file content, not the source path or filename.

Search performs a case-insensitive display-name substring match. Rename changes
only the display name in metadata, not either audio filename. Duplicate display
names are allowed. Remove asks for confirmation and deletes metadata plus the
managed copy; if managed-file deletion fails, Soundboard reports the failure
and rolls the library back instead of claiming success.

Missing or unreadable managed files are skipped during startup with a warning
that identifies the metadata and local storage path. Restore the complete
library from backup or repair the affected metadata while Soundboard is
closed.

## One-shot playback

Each explicit tile selection starts that sound from the beginning. The sound
plays once, stops at its natural end, and never loops or automatically
restarts.

- Selecting the playing tile explicitly stops its current session and starts a
  new session from the beginning.
- Selecting another tile stops the current sound and starts the selected sound.
- Only one sound effect is active at a time.
- **Stop sound** stops the effect without stopping the microphone engine.
- Selecting a tile while the engine is stopped shows an instruction to start
  the engine; it never starts routing silently.
- Natural completion returns the tile to idle while the microphone continues.

Each session carries the stable library sound ID and a monotonically increasing
session ID. Mixer end-of-stream queues completion only once. Completion removes
the mixer input, disposes the reader/session, and clears playback only when the
completed session is still current. A stale callback from a stopped or replaced
session cannot alter a newer session.

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

The library accepts WAV and MP3 sources. The mixer supports mono or stereo
microphone/file sources and mono or stereo output. A multichannel output is
rejected rather than silently reinterpreted.

### Crackling or growing latency

The microphone bridge is bounded to 250 ms. On overflow it clears stale audio
and increments the diagnostic overflow count instead of allowing latency to
grow indefinitely. Persistent overflows indicate the machine or driver cannot
service the selected endpoints reliably at that time.

## Current limitations

- one microphone, one VB-CABLE render endpoint, and one optional physical
  sound-only monitor endpoint;
- one WAV or MP3 sound effect at a time;
- mono/stereo sources and mono/stereo output only;
- no categories, favorites, custom tile images, or custom tile colors;
- no hotkeys, profiles, trimming, waveform editor, fades, or normalization;
- no gate, compression, limiter, or other audio processing;
- no microphone or system-audio monitoring through physical headphones;
- no system-audio capture or Discord-output capture;
- no Discord API integration or automatic Discord configuration;
- no cloud sync, SQLite database, tray integration, startup task, installer,
  or automatic updates.

Soundboard does not enable “Listen to this device,” request administrator
rights, install or modify drivers, change Windows defaults, or change Discord
settings automatically. Sound-only monitoring has not been claimed as audibly
verified by automated build validation; perform the manual headset and Discord
checks on the target computer before relying on it.
