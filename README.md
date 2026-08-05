# Soundboard

Soundboard is a local Windows desktop soundboard. It combines a physical
microphone with one-shot sound effects and sends the mix to VB-CABLE through
`CABLE Input`; Discord or a game then receives that combined signal from
`CABLE Output`. Sound effects can also be monitored separately through
physical headphones.

The application works entirely on the local computer. It requires no account,
uses no telemetry or cloud storage, and does not access the network.

## Features

- WAV and MP3 playback
- Ogg Opus and Ogg Vorbis playback, including `.opus` files in an Ogg
  container
- Persistent local sound library
- Empty library on first launch, ready for user-controlled audio imports
- Categories, Favorites, search, and persistent manual ordering
- Drag a tile onto a sidebar category to file it, with an Undo notification
- Organize mode for selecting several sounds and moving or favoriting them
  together in one operation
- Quick "Move to" category action on every tile
- Imports go straight into the selected category, or into a destination you
  pick as part of the import
- Audio files can be dragged from File Explorer onto the grid or onto a
  category
- Inline category creation, renaming, and deletion in the sidebar
- Tile accents and other tile personalization
- Windows global hotkeys using registered keys or key combinations
- One-shot playback with concurrent sounds; re-triggering the same sound
  restarts it from the beginning
- Non-destructive trimming, fade-in, and fade-out
- Decoded waveform editing
- Per-sound volume plus one soundboard master volume
- Automatic microphone startup and endpoint reconnection
- Windows-default communications microphone mode or a pinned physical device
- Sound-only monitoring through physical headphones or speakers
- Duplicate detection using SHA-256
- Accessible keyboard navigation and screen-reader labels

Soundboard never modifies imported source files. It copies supported files
into its managed local library and stores trim, fade, volume, category,
hotkey, and personalization settings as metadata.

Soundboard includes no audio content and does not seed, download, or suggest
sounds. Users add their own audio through the Import Sounds command and are
responsible for using media they are authorized to use. Managed copies remain
local under `%LOCALAPPDATA%\Soundboard`.

## Requirements

- Windows x64
- [VB-CABLE](https://vb-audio.com/Cable/) installed separately for virtual
  microphone routing
- Discord Krisp or similar noise suppression generally disabled so sound
  effects are not filtered out

The installer and portable release are self-contained. No separately installed
.NET runtime is required.

## Installation

Download the current installer or portable archive from [this repository's
Releases page](https://github.com/PabloBratee/soundboard/releases).

### Installer

Run `Soundboard-Setup-v1.2.1-win-x64.exe`. The installer is per-user and shows
a destination page, so the installation folder can be changed. Its normal
default under the current user's local application-data folder does not require
administrator rights.

### Portable ZIP

Extract `Soundboard-v1.2.1-win-x64-portable.zip` to a writable folder and run
`Soundboard\Soundboard.exe`. Portable mode removes the installer requirement;
user data still stays under `%LOCALAPPDATA%\Soundboard`.

The application and installer are unsigned. Windows SmartScreen may display an
Unknown Publisher warning. Verify downloads with the included
`SHA256SUMS.txt` before running them.

## Discord routing

On first launch, keep the recommended Windows-default microphone mode or pin
a physical microphone, then select `CABLE Input` if it was not detected
automatically. Soundboard captures that microphone continuously whenever the
app is open. In Discord or a game, select these devices once:

```text
Discord input:              CABLE Output
Discord output:             Physical headset
```

Do not select VB-CABLE as Discord's output. That can create confusing routing
or feedback; Discord output should remain on the physical headset.

Krisp and similar voice-processing features can suppress sound effects even
when they reach VB-CABLE correctly. Disable them when using the soundboard.
Soundboard does not change Discord or Windows audio settings.

Optionally enable sound-only monitoring in Soundboard and select a physical
headset or speakers. Microphone audio is never sent to this monitor path.

## Privacy

Soundboard has:

- no telemetry or analytics;
- no accounts;
- no cloud sync;
- no audio uploads; and
- no network access.

Global hotkeys use Windows `RegisterHotKey` registrations for explicitly
configured keys or key combinations. Soundboard does not use keylogging,
low-level keyboard hooks, raw input, keyboard polling, or simulated
keystrokes.

Files and settings stay under:

```text
%LOCALAPPDATA%\Soundboard
```

That directory contains `library.json`, `settings.json`, managed copies of
imported sounds, and waveform caches. Back up the
entire directory while Soundboard is closed to preserve the library. Reinstall
and uninstall operations preserve this user-data directory by default.

## Supported audio formats

Soundboard accepts:

- `.wav`
- `.mp3`
- `.ogg` containing Opus audio
- `.ogg` containing Vorbis audio
- `.opus` containing Opus audio in an Ogg container

Ogg is a container. Soundboard inspects its contents and rejects malformed
files, unsupported codecs, video, multiple logical streams, and unsupported
multichannel audio. It does not require FFmpeg, VLC, external converters, or
runtime codec downloads.

## Building from source

Development requires Windows, the .NET 10 SDK, and an x64-capable environment.

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
dotnet run --project .\tests\Soundboard.App.Tests\Soundboard.App.Tests.csproj --configuration Release --no-build --no-restore
```

Run the application from source with:

```powershell
dotnet run --project .\src\Soundboard.App\Soundboard.App.csproj --configuration Release
```

The custom executable test harness is required in addition to `dotnet test`
because the test project contains dependency-free integration checks that run
as a normal executable.

## Packaging

Inno Setup 7 is required to build the installer. From a clean checkout:

```powershell
.\build\package.ps1 -Version 1.2.1
.\build\verify-package.ps1 -Version 1.2.1
```

The packaging script restores, builds, format-checks, runs tests and the custom
harness, publishes a self-contained single-file x64 application, creates the
portable ZIP and per-user installer, and generates the release manifest and
SHA-256 checksums. Generated files are written under the ignored `artifacts`
directory.

## Limitations

- Windows x64 only
- VB-CABLE remains a separate prerequisite
- No automatic updates
- No code signing yet
- No microphone monitoring through the application
- No general system-audio capture
- Mono and stereo sources and outputs only
- No Discord API integration or automatic Discord configuration
- No tray integration or startup task

Soundboard starts its internal audio service automatically, but does not
install or modify drivers, change Windows default audio devices, or change
Discord settings. Windows does not expose a supported application API for
publishing a mixed stream through an existing physical microphone endpoint;
see [the architecture decision](docs/audio-architecture.md).

## License

Soundboard source code is available under the [MIT License](LICENSE).
Third-party dependencies retain their own licenses; distribution notices and
license texts are under [`release`](release). The project license applies to
the software, not to audio or other media imported by users. Soundboard ships
with no audio content. The installer and portable build are unsigned.

The MIT License does not grant trademark rights to the Soundboard name, logo,
or branding beyond uses necessary to describe the software and exercise the
license. No third-party component is relicensed by this project.
