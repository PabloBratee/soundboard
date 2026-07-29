# Soundboard

Soundboard is planned as a local Windows desktop application that combines a
physical microphone with user-triggered sound files and sends the mix to a
virtual audio device for use by Discord or games.

## Current scope

This proof of concept only discovers active Windows audio endpoints and displays
them in a diagnostic WPF window. It selects the current default capture endpoint
when available and prefers a render endpoint whose name looks like VB-CABLE.
Refreshing only repeats device discovery; the application does not change
Windows audio settings.

This version does not capture, play, transmit, resample, or mix audio.

## Technology

- C# with nullable reference types and implicit usings
- .NET 10 WPF targeting `net10.0-windows`
- x64 Windows
- NAudio 2.3.0 and Windows Core Audio APIs
- SDK-style projects and Git

## Prerequisites

- Windows 10 or Windows 11 on x64 hardware
- .NET 10 SDK with the Windows Desktop tooling
- Network access to NuGet during the first package restore
- Optional: VB-CABLE or a similar virtual audio cable for routing tests

VB-CABLE is not installed or configured by this application.

## Restore, build, and run

From the repository root:

```powershell
dotnet restore
dotnet build --configuration Release
dotnet run --project .\src\Soundboard.App\Soundboard.App.csproj --configuration Release
```

## Audio endpoint directions

A **capture endpoint** supplies audio to an application. A physical microphone
is a capture endpoint. With VB-CABLE, Discord or a game would later select the
capture endpoint commonly named `CABLE Output` as its microphone.

A **render endpoint** accepts audio from an application. Soundboard will
eventually write its mixed signal to a virtual render endpoint commonly named
`CABLE Input`.

The intended future route is:

```text
Physical microphone + sound file
    -> Soundboard mix
    -> CABLE Input (render endpoint)
    -> CABLE Output (capture endpoint)
    -> Discord or game microphone input
```

## Next milestone

The next intended milestone is microphone and sound-file mixing into the
selected render endpoint. That functionality is not implemented in this
proof of concept.
