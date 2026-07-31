# Contributing

Soundboard is a Windows x64 WPF application targeting .NET 10. Development and
validation therefore require Windows and the .NET 10 SDK.

Before opening a pull request, run:

```powershell
dotnet restore
dotnet build --configuration Release
dotnet format Soundboard.sln --verify-no-changes --no-restore
dotnet test --configuration Release --no-build
dotnet run --project .\tests\Soundboard.App.Tests\Soundboard.App.Tests.csproj --configuration Release --no-build --no-restore
git diff --check
```

Keep changes focused and follow the formatting already used by the C# and WPF
XAML code. Preserve the established audio-routing safety boundaries: virtual
microphone output must not target physical playback devices, sound-only
monitoring must not receive microphone audio, and the audio engine must remain
an explicit user action.

Do not commit user audio, `library.json`, `settings.json`, caches, credentials,
certificates, or generated build and release artifacts. Do not add a native
dependency without a clear technical and licensing justification.

Pull requests should explain the user-visible change, the safety impact, and
the exact automated and interactive validation performed. Do not claim
headset, Discord, device, or audible behavior that was not tested.
