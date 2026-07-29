# Soundboard

Soundboard is a local Windows desktop application that combines a physical
microphone with a selected sound file and sends the mix to a VB-CABLE virtual
audio endpoint for use by Discord or a game.

## Current scope

This milestone is an audio-mixing proof of concept, not a complete soundboard.
It provides:

- active Windows capture and render endpoint discovery;
- endpoint selection by persistent Windows Core Audio endpoint ID;
- live microphone capture through WASAPI shared mode;
- microphone volume from 0% to 200% and mute;
- one selected WAV or MP3 mixed with the microphone;
- sound volume from 0% to 200%, replay, and early stop;
- live microphone and final-output peak meters;
- bounded microphone buffering and format diagnostics; and
- WASAPI shared-mode output to a likely VB-CABLE render endpoint.

The engine intentionally refuses physical speakers and headsets as output
targets. There is no override in this milestone because feeding a live
microphone to physical speakers can cause loud feedback.

## Technology

- C# with nullable reference types and implicit usings
- .NET 10 WPF targeting `net10.0-windows`
- x64 Windows
- NAudio 2.3.0 and Windows Core Audio APIs
- SDK-style projects and Git

NAudio is pinned to stable version 2.3.0. Do not replace it with a prerelease.

## VB-CABLE prerequisite

Install VB-CABLE separately and complete any restart requested by its installer
before using the audio engine. Soundboard does not install or configure an
audio driver.

VB-CABLE names the two sides from the perspective of ordinary audio
applications:

- `CABLE Input` is a **render endpoint**. Soundboard writes its microphone and
  file mix here.
- `CABLE Output` is the related **capture endpoint**. Discord or a game selects
  this as its microphone.

The route is:

```text
Physical microphone ─┐
                     ├─ Soundboard mixer → CABLE Input
Selected sound file ─┘

CABLE Output → Discord or game microphone
```

Friendly names can vary, so Soundboard discovers devices and then retains the
selected Windows endpoint IDs. It will not start unless it finds both a likely
VB-CABLE render endpoint and a related active VB-CABLE capture endpoint.

## Restore, build, and run

From the repository root:

```powershell
dotnet restore
dotnet build --configuration Release
dotnet run --project .\src\Soundboard.App\Soundboard.App.csproj --configuration Release
```

## Start the audio engine

1. Select the physical microphone.
2. Select the normal VB-CABLE render endpoint, usually
   `CABLE Input (VB-Audio Virtual Cable)`.
3. Select **Start audio engine**.
4. Speak and verify that both the microphone and final-output meters move.
5. Use microphone volume or mute as needed.
6. Select **Stop audio engine** before changing or refreshing devices.

The selected output's shared-mode mix format determines the mixer sample rate
and channel count. The mixer is 32-bit IEEE floating point and supports mono or
stereo output. Microphone input is converted from its native format and
resampled or channel-converted when necessary.

## Play a WAV or MP3

1. Select **Choose sound file** and choose a local `.wav` or `.mp3` file.
2. Start the audio engine if it is not already running.
3. Select **Play sound**.
4. Select **Stop sound** to end the file without stopping the microphone.

Only one file instance can play at a time. Pressing Play after completion starts
the file again from the beginning. Readers are opened for each playback and
disposed after completion or stop; files are not copied into the application.
An unreadable file is reported without stopping the microphone engine.

## Configure Discord manually

Soundboard does not change Discord settings. In Discord's Voice & Video
settings, select:

- **Input device:** `CABLE Output (VB-Audio Virtual Cable)`
- **Output device:** the physical headset, for example
  `Speakers (2- Razer BlackShark V2 Pro)`

Do **not** select VB-CABLE as Discord's output device. Discord output routed
back into the cable is outside this milestone and can create an unusable route
or feedback.

Discord noise suppression and voice processing may suppress or distort some
sound effects. If a sound reaches the cable but is missing from Discord's
microphone test, temporarily compare the result with Discord noise suppression
disabled and restore the setting you prefer.

## Troubleshooting

### Missing VB-CABLE endpoints

Both a render endpoint such as `CABLE Input` and a capture endpoint such as
`CABLE Output` must be active. If either is absent, complete VB-CABLE
installation and the required Windows restart, then use **Refresh devices**.
Soundboard neither simulates a virtual endpoint nor installs a driver.

### Device in use or disconnected

Stop other applications that may hold the selected endpoint exclusively, then
stop and restart the Soundboard engine. A device removal or stream failure is
shown in the status area rather than intentionally ignored. Refresh only while
the engine is stopped.

### Unsupported format

This milestone accepts mono and stereo microphone/file sources and a mono or
stereo render mix format. Multichannel output is rejected with a diagnostic;
it is never silently reinterpreted. Select the standard stereo `CABLE Input`
endpoint or configure that endpoint to expose a normal mono/stereo shared-mode
format.

### No meter activity

- Confirm the intended physical microphone is selected and not muted.
- Set microphone volume to 100%.
- Confirm Windows permits desktop applications to access the microphone.
- Confirm the engine state is `Running`.
- Speak into the microphone and check the microphone meter first, then the
  final-output meter.
- For a sound file, confirm Play is active and its volume is above 0%.

### Crackling or growing latency

The microphone bridge is bounded to 250 ms. On overflow it clears stale audio
and reports the overflow count rather than allowing latency to grow
indefinitely. Stop and restart the engine to clear the buffer. Also close
high-load audio applications, confirm both endpoints remain active, and use a
normal mono/stereo shared format. Persistent overflows indicate the machine or
driver cannot service the selected endpoints reliably at the current time.

## Current limitations

- one microphone and one VB-CABLE render endpoint;
- one WAV or MP3 playback instance at a time;
- mono/stereo sources and mono/stereo output only;
- no sound library, tiles, categories, search, favorites, or persistence;
- no hotkeys, trimming, waveform editor, fades, or normalization;
- no noise suppression, automatic gain control, EQ, gate, compression, or
  limiter;
- no microphone or sound monitoring through physical headphones;
- no system-audio or Discord-output capture;
- no Discord API integration or automatic Discord configuration;
- no driver installation, tray integration, startup task, installer, or
  automatic updates; and
- no claim that Discord transmission works until it is manually tested in the
  local Discord client.

Soundboard does not change Windows default playback or recording devices, does
not enable “Listen to this device,” does not request administrator rights, and
does not change Discord settings automatically.
