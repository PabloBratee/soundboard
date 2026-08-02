# Windows audio architecture decision

Research reviewed 2026-08-02. This decision uses documented Windows APIs and
Microsoft's maintained driver samples; it deliberately excludes endpoint-name
hacks, registry edits, undocumented APIs, and silent system-setting changes.

## Decision

An ordinary Windows desktop application cannot publish an application-created
mixed stream through an existing physical microphone capture endpoint. A
physical microphone endpoint is a source that applications capture from, not a
destination another application can render into. Shared versus exclusive mode
changes ownership and format negotiation, but not that direction.

Soundboard therefore keeps a virtual capture endpoint as the destination
applications' stable input. It captures the selected physical microphone,
decodes one-shot files, mixes them in floating point, and renders the combined
stream to VB-CABLE's render endpoint (`CABLE Input`). Discord or a game captures
the corresponding `CABLE Output` endpoint. The app does not install VB-CABLE or
change Windows or per-application defaults.

## Current application path

```text
Physical capture endpoint --WASAPI shared capture--+
                                                    +--float mixer--boundary clamp--WASAPI shared render--> CABLE Input
Decoded/trimmed/faded sound --per-sound x master---+
                                                    +--monitor volume--boundary clamp--> physical headphones (optional)
```

The microphone branch has unity application gain and never enters the monitor
branch. Each sound decoder reaches end-of-stream once; a trigger for the same
sound replaces that sound's current session, while different sounds may mix
concurrently. The only final processing replaces NaN/infinity with zero and
clips samples outside `[-1, 1]`. It does not normalize or alter valid samples.

The internal service starts on application launch, follows the Windows default
communications capture endpoint or a pinned endpoint ID, listens for endpoint
notifications, and reconnects after device/default changes and resume.

## Endpoint identity and recovery

Persisted Core Audio endpoint IDs are authoritative. Discovery also reads the
device-interface friendly name, endpoint description, controller device ID,
and interface path. VB-CABLE classification prefers its driver/interface
identity (`VB-Audio Virtual Cable` / `VBAudioVAC`) and uses the familiar
`CABLE Input` / `CABLE Output` names only as a compatibility fallback and for
display. This avoids excluding an unrelated physical microphone merely because
its user-visible name contains a generic word such as `Cable`.

The selected render endpoint is paired with a capture endpoint from the same
controller device instance when that metadata is available. Duplicate cable
endpoints therefore remain distinct, and a saved endpoint ID continues to win
even if its display name changes. If a saved virtual render endpoint is absent,
Soundboard preserves the ID and waits in a clear recovery state; it never
silently redirects the combined microphone stream to another render device.

## User volume mapping

Per-sound and soundboard-master percentages both use the same squared audio
taper:

```text
amplitude = (percentage / 100)^2
```

The exact reference points are 0% = silence, 25% = 0.0625 (-24.08 dB),
50% = 0.25 (-12.04 dB), 75% = 0.5625 (-5.00 dB), and 100% = unity. The curve
is monotonic and never exceeds unity. It provides finer adjustment near the
top than a linear-amplitude slider while retaining a simple, predictable
mapping; no normalization or compensating gain is hidden behind it.

Each sound branch applies its per-sound gain and master gain once. Live slider
changes use a five-millisecond linear transition to avoid a single-sample
discontinuity, reaching exact silence or unity at the end of the transition.
The physical microphone bypasses both gains. Preview uses the identical
per-sound-times-master source gain, while monitoring adds only its explicit,
separate monitor-volume gain.

## API findings

- [Windows Core Audio](https://learn.microsoft.com/en-us/windows/win32/coreaudio/about-the-windows-core-audio-apis)
  exposes capture endpoints for recording and render endpoints for playback.
  [WASAPI exclusive mode](https://learn.microsoft.com/en-us/windows/win32/coreaudio/exclusive-mode-streams)
  gives a client exclusive endpoint access; it does not make a capture endpoint
  writable.
- [WASAPI loopback capture](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
  captures the system mix from a *render* endpoint in shared mode. It is useful
  for recording speaker output, not for injecting into a physical microphone,
  and would risk capturing unrelated desktop audio.
- [AudioGraph](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/audio-graphs)
  can connect device/file/frame input nodes to device/file/frame output nodes.
  Its device output is a render device; it does not publish through an existing
  capture endpoint.
- [IMMNotificationClient](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient)
  provides supported add/remove/state/property/default-device notifications.
  Soundboard uses this mechanism and keeps callbacks non-blocking.
- [Audio Processing Objects](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-processing-object-architecture)
  run in a device's audio engine pipeline. A custom APO is registered through
  a componentized audio driver/APO INF and is tied to the target device; it is
  not an ordinary per-user application plug-in and is inappropriate for
  replacing arbitrary physical microphones.
- A software capture endpoint is driver work. Microsoft's maintained
  [SYSVAD virtual audio sample](https://github.com/microsoft/windows-driver-samples/tree/main/audio/sysvad)
  demonstrates the WDM/WaveRT approach. [ACX](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/acx-audio-class-extensions-overview)
  is the newer audio class extension framework and currently supports WaveRT
  streaming. Neither turns this into a normal desktop API.

## Alternatives and implications

Application-specific integrations could feed a destination that explicitly
accepts an SDK/plugin stream, but every voice application would need separate
support. Hardware mixers/interfaces with loopback can expose a combined input,
but behavior and setup are device-specific.

A custom virtual microphone could provide branding and bundled setup, but it
would add a kernel/audio driver, elevated Plug and Play installation, Windows
driver signing and Hardware Dev Center submission, OS compatibility testing,
update servicing, and explicit uninstall/rollback. Microsoft documents current
[driver signing options](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings),
[PnPUtil driver installation and deletion](https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/pnputil-examples),
and [driver-package uninstall behavior](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/using-device-manager-to-uninstall-devices-and-driver-packages).
That cost and system risk are not justified merely to replace the one-time
selection of `CABLE Output` in a destination application.

## User model

The UI calls the physical capture endpoint **Your microphone** and the virtual
capture endpoint selected in Discord/games **Soundboard microphone**. Internal
capture/render graphs remain troubleshooting details. Normal use is: complete
the short first-run setup, select `CABLE Output` once in the destination, then
launch Soundboard and trigger sounds without a manual start/stop step.
