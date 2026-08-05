#ifndef AppVersion
  #define AppVersion "1.2.1"
#endif
#ifndef NumericVersion
  #define NumericVersion "1.2.1.0"
#endif
#ifndef DistributionDir
  #define DistributionDir "..\artifacts\staging\Soundboard-v1.2.1-win-x64\Soundboard"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\release"
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "Soundboard-Setup-v1.2.1-win-x64"
#endif

#define ProductName "Soundboard"
#define PublisherName "Pablo"
#define ProductMutex "Local\Pablo.Soundboard.Application.5E44D118-81F6-4BC8-B960-4FD04D09883A"

[Setup]
AppId={{5E44D118-81F6-4BC8-B960-4FD04D09883A}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher={#PublisherName}
VersionInfoVersion={#NumericVersion}
VersionInfoCompany={#PublisherName}
VersionInfoDescription={#ProductName} per-user installer
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Soundboard
DisableDirPage=no
AppendDefaultDirName=yes
AlwaysShowDirOnReadyPage=yes
DefaultGroupName=Soundboard
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=..\src\Soundboard.App\Assets\Soundboard.ico
UninstallDisplayIcon={app}\Soundboard.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile=Prerequisites.txt
AppMutex={#ProductMutex}
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
ChangesAssociations=no
ChangesEnvironment=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; \
    GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#DistributionDir}\Soundboard.exe"; DestDir: "{app}"; \
    Flags: ignoreversion
Source: "{#DistributionDir}\README.txt"; DestDir: "{app}"; \
    Flags: ignoreversion
Source: "{#DistributionDir}\LICENSE.txt"; DestDir: "{app}"; \
    Flags: ignoreversion
Source: "{#DistributionDir}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; \
    Flags: ignoreversion
Source: "{#DistributionDir}\licenses\*"; DestDir: "{app}\licenses"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Soundboard"; Filename: "{app}\Soundboard.exe"; \
    WorkingDir: "{app}"
Name: "{autodesktop}\Soundboard"; Filename: "{app}\Soundboard.exe"; \
    WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Soundboard.exe"; Description: "Launch Soundboard"; \
    Flags: nowait postinstall skipifsilent
