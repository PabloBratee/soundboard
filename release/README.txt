SOUNDBOARD 1.0.0 FOR WINDOWS X64
================================

Soundboard is a local Windows soundboard with virtual microphone mixing.

PREREQUISITES
-------------

* Windows x64.
* VB-CABLE must be installed separately for virtual microphone routing.
  VB-CABLE is not included and Soundboard does not install drivers.
* Discord noise suppression, including Krisp, normally must be disabled so
  sound effects are not filtered out.
* No separate .NET runtime installation is required. This release is
  self-contained.

INSTALLATION
------------

Installer:
Run Soundboard-Setup-v1.0.0-win-x64.exe. The per-user installer's Select
Destination Location page displays this recommended default:

%LOCALAPPDATA%\Programs\Soundboard

That displayed location is only the default. You may browse to or enter
another writable folder, and setup shows the chosen location again on the
Ready to Install page. Installing into a user-writable folder does not require
administrator rights.

Portable ZIP:
Extract the complete ZIP to a folder you can write to, then run
Soundboard\Soundboard.exe. "Portable" means no installer is required and the
application is self-contained. It does not mean user data is stored beside
the executable.

On first launch, open Settings and select:

1. Your physical microphone.
2. CABLE Input as the Soundboard virtual output.
3. An optional physical headset or speakers for sound-only monitoring.

Soundboard never starts the audio engine automatically. Select Start engine
after reviewing the selected devices.

DISCORD ROUTING
---------------

Soundboard virtual output: CABLE Input
Discord input:             CABLE Output
Discord output:            Physical headset

Soundboard does not change Discord or Windows audio settings.

USER DATA AND BACKUP
--------------------

Application files:
User-selected installation directory

User data:
%LOCALAPPDATA%\Soundboard

Soundboard stores user data in:

%LOCALAPPDATA%\Soundboard

That directory contains imported managed audio, library.json, settings.json,
the waveform cache, and the loudness-analysis cache. Backing up the entire
Soundboard directory preserves the library and settings. Changing the
application installation folder does not move any of this user data.

UPGRADES AND UNINSTALL
----------------------

Close Soundboard before installing or upgrading. The stable per-user installer
identity keeps one Apps & Features entry. Reinstall and upgrade initially show
the previously selected installation folder, and the destination page remains
available so you can change it.

Uninstall removes installer-owned program files and shortcuts. It preserves
%LOCALAPPDATA%\Soundboard by default so reinstalling retains the library and
settings. To remove all Soundboard data after uninstalling, delete only this
specific directory:

%LOCALAPPDATA%\Soundboard

Do not delete %LOCALAPPDATA% itself.

SIGNING AND UPDATES
-------------------

Soundboard.exe and the installer are currently unsigned. Windows SmartScreen
or an Unknown Publisher warning may appear. Future public distribution should
use a trusted Authenticode certificate with timestamping.

Soundboard 1.0.0 does not include automatic updates or background update
checks.

PROJECT LICENSE
---------------

Soundboard source code and the project-owned application are licensed under
the MIT License. See LICENSE.txt included with this distribution.

Third-party components retain their own licenses. See
THIRD-PARTY-NOTICES.txt and the licenses directory. The MIT License does not
grant trademark rights to the Soundboard name, logo, or branding beyond uses
necessary to describe the software and exercise the license.
