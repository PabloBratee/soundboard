[CmdletBinding()]
param(
    [Parameter()]
    [string]$Version = '1.3.1',

    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$audioRedesignCommit = 'c59234defb2e5791995b7c5a67ff47bedbfefb67'

Add-Type -AssemblyName System.IO.Compression

function Assert-Condition {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (!$Condition) {
        throw $Message
    }
}

function Assert-File {
    param([Parameter(Mandatory)][string]$Path)

    Assert-Condition (
        Test-Path -LiteralPath $Path -PathType Leaf
    ) "Expected artifact is missing: $Path"
}

function Assert-NoMachineSpecificPaths {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string[]]$ForbiddenRoots
    )

    $binaryText = [System.Text.Encoding]::Latin1.GetString(
        [System.IO.File]::ReadAllBytes($Path))
    foreach ($root in $ForbiddenRoots) {
        if ([string]::IsNullOrWhiteSpace($root)) {
            continue
        }

        Assert-Condition (
            !$binaryText.Contains(
                $root,
                [StringComparison]::OrdinalIgnoreCase)
        ) "Packaged binary contains a machine-specific build path."
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory)][string]$Path)

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        Assert-Condition ($reader.ReadUInt16() -eq 0x5A4D) (
            "'$Path' does not have an MZ header.")
        $stream.Position = 0x3C
        $peOffset = $reader.ReadInt32()
        Assert-Condition (
            $peOffset -gt 0 -and $peOffset -lt ($stream.Length - 6)
        ) "'$Path' has an invalid PE offset."
        $stream.Position = $peOffset
        Assert-Condition ($reader.ReadUInt32() -eq 0x00004550) (
            "'$Path' does not have a PE header.")
        return $reader.ReadUInt16()
    }
    finally {
        $reader.Dispose()
    }
}

if ($Version -notmatch
    '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "Version '$Version' is not a valid semantic version."
}

$numericVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$releaseRoot = Join-Path $repositoryRoot 'artifacts\release'
$portableFileName = "Soundboard-v$Version-win-x64-portable.zip"
$installerFileName = "Soundboard-Setup-v$Version-win-x64.exe"
$portableArchive = Join-Path $releaseRoot $portableFileName
$installerArtifact = Join-Path $releaseRoot $installerFileName
$manifestPath = Join-Path $releaseRoot 'release-manifest.json'
$checksumsPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$installerSource = Join-Path $repositoryRoot 'installer\Soundboard.iss'
$forbiddenMachineRoots = @(
    $repositoryRoot
    [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::UserProfile)
) | Sort-Object -Unique

Assert-File $portableArchive
Assert-File $manifestPath
Assert-File $checksumsPath
Assert-File $installerSource
if (!$SkipInstaller) {
    Assert-File $installerArtifact
}
else {
    Assert-Condition (!(Test-Path -LiteralPath $installerArtifact)) (
        "Installer '$installerFileName' exists despite -SkipInstaller.")
}

$installerText = Get-Content -LiteralPath $installerSource -Raw
$requiredInstallerSourcePatterns = @(
    'AppId=\{\{5E44D118-81F6-4BC8-B960-4FD04D09883A\}'
    'ArchitecturesAllowed=x64compatible'
    'ArchitecturesInstallIn64BitMode=x64compatible'
    'MinVersion=10\.0'
    '#define ProductMutex "Local\\Pablo\.Soundboard\.Application\.5E44D118-81F6-4BC8-B960-4FD04D09883A"'
    'AppMutex=\{#ProductMutex\}'
    'CloseApplications=no'
    'RestartApplications=no'
    'DefaultGroupName=Soundboard'
    'Name: "desktopicon".*Flags: unchecked'
    'Name: "\{group\}\\Soundboard"'
    'Flags: nowait postinstall skipifsilent'
)
foreach ($pattern in $requiredInstallerSourcePatterns) {
    Assert-Condition ($installerText -match "(?ms)$pattern") (
        "Installer source is missing required configuration '$pattern'.")
}
$setupSection = [regex]::Match(
    $installerText,
    '(?ms)^\[Setup\]\s*(.*?)(?=^\[|\z)')
Assert-Condition $setupSection.Success (
    'Installer source is missing the [Setup] section.')
$requiredSetupDirectives = [ordered]@{
    'AppId' = '{{5E44D118-81F6-4BC8-B960-4FD04D09883A}'
    'DefaultDirName' = '{localappdata}\Programs\Soundboard'
    'DisableDirPage' = 'no'
    'AppendDefaultDirName' = 'yes'
    'AlwaysShowDirOnReadyPage' = 'yes'
    'UsePreviousAppDir' = 'yes'
    'PrivilegesRequired' = 'lowest'
}
foreach ($directive in $requiredSetupDirectives.GetEnumerator()) {
    $directiveMatches = [regex]::Matches(
        $setupSection.Groups[1].Value,
        "(?im)^\s*$([regex]::Escape($directive.Key))\s*=\s*([^\r\n;]+?)\s*$")
    Assert-Condition ($directiveMatches.Count -eq 1) (
        "Installer [Setup] must define '$($directive.Key)' exactly once.")
    $actualValue = $directiveMatches[0].Groups[1].Value.Trim()
    Assert-Condition ($actualValue -eq $directive.Value) (
        "Installer [Setup] directive '$($directive.Key)' is '$actualValue', " +
        "expected '$($directive.Value)'.")
}
Assert-Condition (
    $installerText -notmatch
    '(?im)^\s*PrivilegesRequiredOverridesAllowed\s*='
) 'Installer source must not allow a machine-wide or elevated scope override.'
Assert-Condition ($installerText -notmatch '(?im)^\[UninstallDelete\]') (
    'Installer source must not delete the user-data directory.')
Assert-Condition ($installerText -notmatch '(?im)^\[Registry\]') (
    'Installer source must not add custom registry entries.')
Assert-Condition (
    $installerText -notmatch
    '(?im)\{(?:user|common)startup\}|taskkill|schtasks|sc\.exe|net\.exe'
) 'Installer source must not add startup entries, services, tasks, or forced process termination.'
Assert-Condition (
    $installerText -notmatch
    '(?im)^\s*(?:SignTool|SignedUninstaller|ISSigAllowedKeys)\s*='
) 'Installer source must not claim or configure signing.'

$expectedZipFiles = @(
    'Soundboard/Soundboard.exe'
    'Soundboard/LICENSE.txt'
    'Soundboard/README.txt'
    'Soundboard/THIRD-PARTY-NOTICES.txt'
    'Soundboard/licenses/NAudio.txt'
    'Soundboard/licenses/NVorbis.txt'
    'Soundboard/licenses/Concentus.txt'
    'Soundboard/licenses/Concentus.Oggfile.txt'
) | Sort-Object
$forbiddenExtensions = @(
    '.pdb', '.cs', '.csproj', '.sln', '.json', '.config', '.log',
    '.wav', '.mp3', '.ogg', '.opus', '.flac', '.m4a', '.aac', '.wma')
$forbiddenNames = @(
    'library.json', 'settings.json', 'project.assets.json',
    'Soundboard.App.Tests.dll')
$forbiddenPackagedText =
    '(?i)(?:(?<![A-Za-z])[A-Z]:[\\/]|StarterLibrary|' +
    'myinstants\.com|youtube\.com|youtu\.be)'

$zipStream = [System.IO.File]::OpenRead($portableArchive)
$zip = [System.IO.Compression.ZipArchive]::new(
    $zipStream,
    [System.IO.Compression.ZipArchiveMode]::Read,
    $false)
$temporaryRoot = [System.IO.Path]::Combine(
    [System.IO.Path]::GetTempPath(),
    "Soundboard-PackageVerification-$([Guid]::NewGuid().ToString('N'))")
try {
    $zipFiles = [System.Collections.Generic.List[string]]::new()
    $executableEntry = $null
    foreach ($entry in $zip.Entries) {
        $entryPath = $entry.FullName.Replace('\', '/')
        Assert-Condition (
            !$entryPath.StartsWith('/') -and
            $entryPath -notmatch '^[A-Za-z]:' -and
            @($entryPath.Split('/')) -notcontains '..'
        ) "ZIP contains unsafe path '$entryPath'."

        if ([string]::IsNullOrEmpty($entry.Name)) {
            continue
        }

        $zipFiles.Add($entryPath)
        $extension = [System.IO.Path]::GetExtension($entry.Name)
        Assert-Condition (
            $extension.ToLowerInvariant() -notin $forbiddenExtensions
        ) "ZIP contains forbidden development or user file '$entryPath'."
        Assert-Condition (
            $entry.Name -notin $forbiddenNames -and
            $entryPath -notmatch
                '(?i)(waveform|analysis)[-/\\]?cache|starter.?library'
        ) "ZIP contains forbidden user or test data '$entryPath'."

        if ($extension.Equals('.txt', [StringComparison]::OrdinalIgnoreCase)) {
            $reader = [System.IO.StreamReader]::new($entry.Open(), $true)
            try {
                $text = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
            Assert-Condition ($text -notmatch $forbiddenPackagedText) (
                "ZIP text file contains a source URL, starter-library " +
                "reference, or machine-specific path: '$entryPath'.")
        }

        if ($entryPath -eq 'Soundboard/Soundboard.exe') {
            $executableEntry = $entry
        }
    }

    Assert-Condition (
        (@($zipFiles | Sort-Object) -join "`n") -eq
        ($expectedZipFiles -join "`n")
    ) "Portable ZIP contents do not match the expected clean file set."
    Assert-Condition ($null -ne $executableEntry) (
        'Portable ZIP does not contain Soundboard.exe.')

    [System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $temporaryExecutable = Join-Path $temporaryRoot 'Soundboard.exe'
    $inputStream = $executableEntry.Open()
    $outputStream = [System.IO.File]::Create($temporaryExecutable)
    try {
        $inputStream.CopyTo($outputStream)
    }
    finally {
        $outputStream.Dispose()
        $inputStream.Dispose()
    }

    $versionInfo = (
        Get-Item -LiteralPath $temporaryExecutable).VersionInfo
    Assert-Condition ($versionInfo.FileVersion -eq $numericVersion) (
        "Soundboard.exe file version '$($versionInfo.FileVersion)' does not " +
        "match '$numericVersion'.")
    Assert-Condition ($versionInfo.ProductVersion -eq $Version) (
        "Soundboard.exe product version '$(
            $versionInfo.ProductVersion)' does not match '$Version'.")
    Assert-Condition ($versionInfo.ProductName -eq 'Soundboard') (
        "Soundboard.exe product name is '$($versionInfo.ProductName)'.")
    Assert-Condition ($versionInfo.CompanyName -eq 'Pablo') (
        "Soundboard.exe company is '$($versionInfo.CompanyName)'.")
    Assert-Condition (
        (Get-PeMachine $temporaryExecutable) -eq 0x8664
    ) 'Soundboard.exe is not an x64 PE image.'
    Assert-NoMachineSpecificPaths `
        -Path $temporaryExecutable `
        -ForbiddenRoots $forbiddenMachineRoots
    $applicationSignature = Get-AuthenticodeSignature -FilePath (
        $temporaryExecutable)
    Assert-Condition (
        $applicationSignature.Status -eq
        [System.Management.Automation.SignatureStatus]::NotSigned
    ) "Soundboard.exe signing status is '$(
        $applicationSignature.Status)', expected NotSigned."
}
finally {
    $zip.Dispose()
    $zipStream.Dispose()
    if (Test-Path -LiteralPath $temporaryRoot) {
        [System.IO.Directory]::Delete($temporaryRoot, $true)
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw |
    ConvertFrom-Json
Assert-Condition ($manifest.product -eq 'Soundboard') (
    'Release manifest has the wrong product.')
Assert-Condition ($manifest.projectLicense -eq 'MIT') (
    'Release manifest does not identify the project license as MIT.')
Assert-Condition (
    $manifest.PSObject.Properties.Name -notcontains 'bundledContentLicensing'
) 'Release manifest must not describe bundled starter content.'
Assert-Condition ($manifest.version -eq $Version) (
    'Release manifest has the wrong version.')
Assert-Condition ($manifest.gitCommit -match '^[0-9a-f]{40}$') (
    'Release manifest Git commit is invalid.')
$currentGitCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
Assert-Condition ($LASTEXITCODE -eq 0) (
    'Could not determine the current Git commit for manifest verification.')
$currentGitStatus = @(
    & git -C $repositoryRoot status --porcelain --untracked-files=normal)
Assert-Condition ($LASTEXITCODE -eq 0) (
    'Could not determine the current Git working-tree state.')
Assert-Condition ($currentGitStatus.Count -eq 0) (
    'Package verification requires a clean Git working tree.')
Assert-Condition ($manifest.gitCommit -eq $currentGitCommit) (
    'Release manifest does not identify the current committed source.')
Assert-Condition (
    $manifest.sourceCommits.audioRedesign -eq $audioRedesignCommit
) 'Release manifest does not identify the audio-redesign commit.'
Assert-Condition ($manifest.sourceCommits.release -eq $currentGitCommit) (
    'Release manifest does not identify the release commit.')
& git -C $repositoryRoot merge-base --is-ancestor `
    $manifest.sourceCommits.audioRedesign $manifest.sourceCommits.release
Assert-Condition ($LASTEXITCODE -eq 0) (
    'The audio-redesign commit is not an ancestor of the release commit.')
Assert-Condition ($manifest.gitWorkingTreeDirty -eq $false) (
    'Release manifest does not report a clean Git working tree.')
Assert-Condition ($manifest.runtimeIdentifier -eq 'win-x64') (
    'Release manifest runtime identifier is not win-x64.')
Assert-Condition (
    $manifest.configuration -eq 'Release' -and
    $manifest.deployment.selfContained -eq $true -and
    $manifest.deployment.singleFile -eq $true -and
    $manifest.deployment.trimmed -eq $false
) 'Release manifest deployment settings are incorrect.'
Assert-Condition ($manifest.signing.application -eq 'unsigned') (
    'Release manifest does not report the application as unsigned.')

$expectedDependencies = [ordered]@{
    'NAudio' = '2.3.0'
    'NAudio.Asio' = '2.3.0'
    'NAudio.Core' = '2.3.0'
    'NAudio.Midi' = '2.3.0'
    'NAudio.Wasapi' = '2.3.0'
    'NAudio.WinForms' = '2.3.0'
    'NAudio.WinMM' = '2.3.0'
    'NVorbis' = '0.10.5'
    'Concentus.Oggfile' = '1.0.7'
    'Concentus' = '2.2.2'
}
foreach ($dependency in $expectedDependencies.GetEnumerator()) {
    Assert-Condition (
        $manifest.dependencies.PSObject.Properties[
            $dependency.Key].Value -eq $dependency.Value
    ) "Release manifest dependency '$($dependency.Key)' is incorrect."
}

$expectedArtifactNames = @($portableFileName)
if (!$SkipInstaller) {
    $expectedArtifactNames += $installerFileName
}
$manifestArtifacts = @($manifest.artifacts)
Assert-Condition (
    (@($manifestArtifacts.fileName | Sort-Object) -join "`n") -eq
    (@($expectedArtifactNames | Sort-Object) -join "`n")
) 'Release manifest artifact list is incorrect.'
foreach ($artifact in $manifestArtifacts) {
    $artifactPath = Join-Path $releaseRoot $artifact.fileName
    Assert-File $artifactPath
    $item = Get-Item -LiteralPath $artifactPath
    $hash = Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256
    Assert-Condition ($item.Length -eq $artifact.byteSize) (
        "Manifest byte size does not match '$($artifact.fileName)'.")
    Assert-Condition (
        $hash.Hash.ToLowerInvariant() -eq $artifact.sha256
    ) "Manifest hash does not match '$($artifact.fileName)'."
}

if (!$SkipInstaller) {
    Assert-Condition ($manifest.installer.compiled -eq $true) (
        'Manifest does not report a compiled installer.')
    Assert-Condition (
        $manifest.installer.detectedVersion -match '^\d+\.\d+\.\d+$'
    ) 'Manifest does not report the detected Inno Setup version.'
    Assert-Condition ($manifest.signing.installer -eq 'unsigned') (
        'Manifest does not report the installer as unsigned.')
    $installerVersion = (
        Get-Item -LiteralPath $installerArtifact).VersionInfo
    $installerFileVersion = $installerVersion.FileVersion.Trim()
    $installerProductVersion = $installerVersion.ProductVersion.Trim()
    Assert-Condition ($installerFileVersion -eq $numericVersion) (
        "Installer file version '$($installerVersion.FileVersion)' does not " +
        "match '$numericVersion'.")
    Assert-Condition ($installerProductVersion -eq $Version) (
        "Installer product version '$(
            $installerVersion.ProductVersion)' does not match '$Version'.")
    Assert-NoMachineSpecificPaths `
        -Path $installerArtifact `
        -ForbiddenRoots $forbiddenMachineRoots
    $installerSignature = Get-AuthenticodeSignature -FilePath (
        $installerArtifact)
    Assert-Condition (
        $installerSignature.Status -eq
        [System.Management.Automation.SignatureStatus]::NotSigned
    ) "Installer signing status is '$(
        $installerSignature.Status)', expected NotSigned."
}
else {
    Assert-Condition (
        $manifest.installer.compiled -eq $false -and
        $manifest.installer.skipped -eq $true -and
        $manifest.signing.installer -eq 'not-built'
    ) 'Portable-only manifest does not honestly report the missing installer.'
}

$checksumEntries = [ordered]@{}
foreach ($line in Get-Content -LiteralPath $checksumsPath) {
    Assert-Condition (
        $line -match '^([0-9a-fA-F]{64})  ([^\\/]+)$'
    ) "Invalid SHA256SUMS line '$line'."
    $checksumEntries[$Matches[2]] = $Matches[1].ToLowerInvariant()
}
$expectedChecksumNames = @($expectedArtifactNames) +
    @('release-manifest.json')
Assert-Condition (
    (@($checksumEntries.Keys | Sort-Object) -join "`n") -eq
    (@($expectedChecksumNames | Sort-Object) -join "`n")
) 'SHA256SUMS.txt contains an unexpected file set.'
foreach ($name in $expectedChecksumNames) {
    $path = Join-Path $releaseRoot $name
    $hash = Get-FileHash -LiteralPath $path -Algorithm SHA256
    Assert-Condition (
        $hash.Hash.ToLowerInvariant() -eq $checksumEntries[$name]
    ) "Checksum does not match '$name'."
}

Write-Output "Package verification passed for Soundboard $Version."
Write-Output "Portable archive: $portableArchive"
if (!$SkipInstaller) {
    Write-Output "Installer: $installerArtifact"
}
else {
    Write-Output 'Installer: not built (-SkipInstaller)'
}
