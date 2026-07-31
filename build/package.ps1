[CmdletBinding()]
param(
    [Parameter()]
    [string]$Version = '1.0.0',

    [switch]$SkipTests,

    [switch]$SkipInstaller,

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-NativeCommand {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "'$FilePath' failed with exit code $LASTEXITCODE."
    }
}

function Get-InnoCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    $candidates = @(
        if ($command) { $command.Source }
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe')
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ } | Select-Object -Unique

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    return $null
}

function Get-InnoCompilerVersion {
    param([Parameter(Mandatory)][string]$CompilerPath)

    $compilerDirectory = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::GetDirectoryName($CompilerPath)
    ).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
    $uninstallRegistryPaths = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
        'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    $registrations = Get-ItemProperty $uninstallRegistryPaths `
        -ErrorAction SilentlyContinue |
        Where-Object {
            $displayNameProperty = $_.PSObject.Properties['DisplayName']
            $installLocationProperty =
                $_.PSObject.Properties['InstallLocation']
            $null -ne $displayNameProperty -and
            $displayNameProperty.Value -like 'Inno Setup version *' -and
            $null -ne $installLocationProperty -and
            ![string]::IsNullOrWhiteSpace($installLocationProperty.Value)
        }
    foreach ($registration in $registrations) {
        $displayVersionProperty =
            $registration.PSObject.Properties['DisplayVersion']
        $registeredDirectory = [System.IO.Path]::GetFullPath(
            $registration.InstallLocation
        ).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
        if ($registeredDirectory.Equals(
                $compilerDirectory,
                [StringComparison]::OrdinalIgnoreCase) -and
            $null -ne $displayVersionProperty -and
            $displayVersionProperty.Value -match '^\d+\.\d+\.\d+$') {
            return $displayVersionProperty.Value
        }
    }

    $fileVersion = (
        Get-Item -LiteralPath $CompilerPath).VersionInfo.ProductVersion
    if ($fileVersion -match '^\d+\.\d+\.\d+$' -and
        $fileVersion -ne '0.0.0') {
        return $fileVersion
    }

    throw "Could not determine the exact Inno Setup version for '$CompilerPath'."
}

function Remove-OwnedPath {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ArtifactRoot
    )

    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($ArtifactRoot)
    $artifactPrefix = $resolvedArtifactRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if ($resolvedPath -ne $resolvedArtifactRoot -and
        !$resolvedPath.StartsWith(
            $artifactPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove non-artifact path '$resolvedPath'."
    }

    if (Test-Path -LiteralPath $resolvedPath) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

function Assert-File {
    param([Parameter(Mandatory)][string]$Path)

    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required source file is missing: $Path"
    }
}

if ($Version -notmatch
    '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-(?:0|[1-9]\d*|[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "Version '$Version' is not a valid semantic version."
}

$numericVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"
$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$solutionPath = Join-Path $repositoryRoot 'Soundboard.sln'
$applicationProject = [System.IO.Path]::Combine(
    $repositoryRoot,
    'src',
    'Soundboard.App',
    'Soundboard.App.csproj')
$testProject = [System.IO.Path]::Combine(
    $repositoryRoot,
    'tests',
    'Soundboard.App.Tests',
    'Soundboard.App.Tests.csproj')
$publishProfile = [System.IO.Path]::Combine(
    $repositoryRoot,
    'src',
    'Soundboard.App',
    'Properties',
    'PublishProfiles',
    'win-x64.pubxml')
$installerSource = [System.IO.Path]::Combine(
    $repositoryRoot,
    'installer',
    'Soundboard.iss')
$releaseSource = Join-Path $repositoryRoot 'release'
$artifactRoot = Join-Path $repositoryRoot 'artifacts'
$artifactStem = "Soundboard-v$Version-win-x64"
$stagingRoot = Join-Path $artifactRoot "staging\$artifactStem"
$portableRoot = Join-Path $stagingRoot 'Soundboard'
$publishRoot = Join-Path $artifactRoot "publish\$artifactStem"
$releaseRoot = Join-Path $artifactRoot 'release'
$portableFileName = "$artifactStem-portable.zip"
$installerFileName = "Soundboard-Setup-v$Version-win-x64.exe"
$portableArchive = Join-Path $releaseRoot $portableFileName
$installerArtifact = Join-Path $releaseRoot $installerFileName
$manifestPath = Join-Path $releaseRoot 'release-manifest.json'
$checksumsPath = Join-Path $releaseRoot 'SHA256SUMS.txt'

$requiredSources = @(
    $solutionPath
    $applicationProject
    $testProject
    $publishProfile
    $installerSource
    (Join-Path $repositoryRoot 'Directory.Build.props')
    (Join-Path $repositoryRoot 'LICENSE')
    (Join-Path $repositoryRoot 'src\Soundboard.App\Assets\Soundboard.ico')
    (Join-Path $releaseSource 'README.txt')
    (Join-Path $releaseSource 'THIRD-PARTY-NOTICES.txt')
    (Join-Path $releaseSource 'licenses\NAudio.txt')
    (Join-Path $releaseSource 'licenses\NVorbis.txt')
    (Join-Path $releaseSource 'licenses\Concentus.txt')
    (Join-Path $releaseSource 'licenses\Concentus.Oggfile.txt')
)
foreach ($source in $requiredSources) {
    Assert-File $source
}

Push-Location $repositoryRoot
try {
    Invoke-NativeCommand 'git' @(
        'rev-parse',
        '--is-inside-work-tree')
    $gitCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $gitCommit -notmatch '^[0-9a-f]{40}$') {
        throw 'Could not determine the current Git commit.'
    }
    $gitStatus = @(& git status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not determine whether the Git working tree is dirty.'
    }
    $gitWorkingTreeDirty = $gitStatus.Count -gt 0

    if ($Clean) {
        Remove-OwnedPath $artifactRoot $artifactRoot
    }
    else {
        Remove-OwnedPath $stagingRoot $artifactRoot
        Remove-OwnedPath $publishRoot $artifactRoot
        foreach ($releaseFile in @(
            $portableArchive,
            $installerArtifact,
            $manifestPath,
            $checksumsPath)) {
            Remove-OwnedPath $releaseFile $artifactRoot
        }
    }

    [System.IO.Directory]::CreateDirectory($portableRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($publishRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($releaseRoot) | Out-Null

    $versionProperties = @(
        "-p:Version=$Version"
        "-p:AssemblyVersion=$numericVersion"
        "-p:FileVersion=$numericVersion"
        "-p:InformationalVersion=$Version"
    )

    Invoke-NativeCommand 'dotnet' @(
        'restore',
        $solutionPath)
    Invoke-NativeCommand 'dotnet' @(
        'restore',
        $applicationProject,
        '--runtime',
        'win-x64')

    $assetsPath = [System.IO.Path]::Combine(
        (Split-Path $applicationProject),
        'obj',
        'project.assets.json')
    Assert-File $assetsPath
    $assets = Get-Content -LiteralPath $assetsPath -Raw |
        ConvertFrom-Json
    $resolvedLibraries = @(
        $assets.libraries.PSObject.Properties.Name)
    $dependencies = [ordered]@{
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
    foreach ($dependency in $dependencies.GetEnumerator()) {
        $libraryKey = "$($dependency.Key)/$($dependency.Value)"
        if ($libraryKey -notin $resolvedLibraries) {
            throw "Expected dependency '$libraryKey' was not resolved."
        }
    }

    Invoke-NativeCommand 'dotnet' (@(
        'build',
        $solutionPath,
        '--configuration',
        'Release',
        '--no-restore') + $versionProperties)

    if (!$SkipTests) {
        Invoke-NativeCommand 'dotnet' @(
            'format',
            $solutionPath,
            '--verify-no-changes',
            '--no-restore')
        Invoke-NativeCommand 'dotnet' (@(
            'test',
            $solutionPath,
            '--configuration',
            'Release',
            '--no-build',
            '--no-restore') + $versionProperties)
        Invoke-NativeCommand 'dotnet' (@(
            'run',
            '--project',
            $testProject,
            '--configuration',
            'Release',
            '--no-build',
            '--no-restore') + $versionProperties)
        Invoke-NativeCommand 'git' @('diff', '--check')
    }

    Invoke-NativeCommand 'dotnet' (@(
        'publish',
        $applicationProject,
        '--configuration',
        'Release',
        '--runtime',
        'win-x64',
        '--self-contained',
        'true',
        '--no-restore',
        '--output',
        $publishRoot,
        "-p:PublishProfile=$publishProfile",
        '-p:DebugSymbols=false',
        '-p:DebugType=None') + $versionProperties)

    $publishedExecutable = Join-Path $publishRoot 'Soundboard.exe'
    Assert-File $publishedExecutable
    $unexpectedPublishFiles = @(
        Get-ChildItem -LiteralPath $publishRoot -File |
            Where-Object { $_.Name -ne 'Soundboard.exe' })
    if ($unexpectedPublishFiles.Count -gt 0) {
        throw "Unexpected publish files: $(
            $unexpectedPublishFiles.Name -join ', ')"
    }

    $versionInfo = (Get-Item -LiteralPath $publishedExecutable).VersionInfo
    if ($versionInfo.FileVersion -ne $numericVersion) {
        throw "Soundboard.exe file version '$(
            $versionInfo.FileVersion)' does not match '$numericVersion'."
    }
    if ($versionInfo.ProductVersion -ne $Version) {
        throw "Soundboard.exe product version '$(
            $versionInfo.ProductVersion)' does not match '$Version'."
    }
    $applicationSignature = Get-AuthenticodeSignature -FilePath (
        $publishedExecutable)
    if ($applicationSignature.Status -ne
        [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "Expected an unsigned application, but signature status is '$(
            $applicationSignature.Status)'."
    }

    Copy-Item -LiteralPath $publishedExecutable -Destination (
        Join-Path $portableRoot 'Soundboard.exe')
    $portableReadme = (
        Get-Content -LiteralPath (
            Join-Path $releaseSource 'README.txt') -Raw
    ).Replace('1.0.0', $Version)
    Set-Content -LiteralPath (
        Join-Path $portableRoot 'README.txt') -Value $portableReadme -Encoding utf8
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot 'LICENSE'
    ) -Destination (Join-Path $portableRoot 'LICENSE.txt')
    Copy-Item -LiteralPath (
        Join-Path $releaseSource 'THIRD-PARTY-NOTICES.txt'
    ) -Destination $portableRoot
    Copy-Item -LiteralPath (
        Join-Path $releaseSource 'licenses'
    ) -Destination $portableRoot -Recurse

    Compress-Archive -LiteralPath $portableRoot -DestinationPath (
        $portableArchive) -CompressionLevel Optimal

    $innoCompiler = Get-InnoCompiler
    $installerTechnologyVersion = $null
    $installerCompiled = $false
    if ($innoCompiler) {
        $installerTechnologyVersion = Get-InnoCompilerVersion $innoCompiler
    }

    if (!$SkipInstaller) {
        if (!$innoCompiler) {
            throw @"
Inno Setup compiler ISCC.exe was not found on PATH or in the standard Inno
Setup 7/6 installation directories. The portable ZIP was created, but a real
installer cannot be compiled. Install Inno Setup 7 separately, then rerun this
command. Use -SkipInstaller only for an explicitly portable-only build.
"@
        }

        Invoke-NativeCommand $innoCompiler @(
            "/DAppVersion=$Version"
            "/DNumericVersion=$numericVersion"
            "/DDistributionDir=$portableRoot"
            "/DOutputDir=$releaseRoot"
            "/DOutputBaseFilename=$(
                [System.IO.Path]::GetFileNameWithoutExtension(
                    $installerFileName))"
            $installerSource)
        Assert-File $installerArtifact
        $installerSignature = Get-AuthenticodeSignature -FilePath (
            $installerArtifact)
        if ($installerSignature.Status -ne
            [System.Management.Automation.SignatureStatus]::NotSigned) {
            throw "Expected an unsigned installer, but signature status is '$(
                $installerSignature.Status)'."
        }
        $installerCompiled = $true
    }

    $artifactFiles = [System.Collections.Generic.List[object]]::new()
    foreach ($artifactPath in @(
        $portableArchive
        if ($installerCompiled) { $installerArtifact }
    )) {
        $item = Get-Item -LiteralPath $artifactPath
        $hash = Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256
        $artifactFiles.Add([ordered]@{
            fileName = $item.Name
            byteSize = $item.Length
            sha256 = $hash.Hash.ToLowerInvariant()
        })
    }

    $manifest = [ordered]@{
        product = 'Soundboard'
        projectLicense = 'MIT'
        version = $Version
        gitCommit = $gitCommit
        gitWorkingTreeDirty = $gitWorkingTreeDirty
        buildUtc = [DateTime]::UtcNow.ToString('o')
        runtimeIdentifier = 'win-x64'
        configuration = 'Release'
        deployment = [ordered]@{
            mode = if ($installerCompiled) {
                @('portable', 'per-user installer')
            }
            else {
                @('portable')
            }
            selfContained = $true
            singleFile = $true
            trimmed = $false
            readyToRun = $false
        }
        dependencies = $dependencies
        artifacts = $artifactFiles
        signing = [ordered]@{
            application = 'unsigned'
            installer = if ($installerCompiled) {
                'unsigned'
            }
            else {
                'not-built'
            }
        }
        installer = [ordered]@{
            technology = 'Inno Setup'
            detectedVersion = $installerTechnologyVersion
            compiled = $installerCompiled
            skipped = [bool]$SkipInstaller
            appId = '{5E44D118-81F6-4BC8-B960-4FD04D09883A}'
            installationScope = 'per-user'
            defaultDirectory = '%LOCALAPPDATA%\Programs\Soundboard'
        }
    }
    $manifest | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $manifestPath -Encoding utf8

    $checksumTargets = @(
        $artifactFiles | ForEach-Object {
            Join-Path $releaseRoot $_.fileName
        }
    ) + @($manifestPath)
    $checksumLines = foreach ($target in $checksumTargets) {
        $hash = Get-FileHash -LiteralPath $target -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $(
            [System.IO.Path]::GetFileName($target))"
    }
    Set-Content -LiteralPath $checksumsPath -Value (
        $checksumLines) -Encoding ascii

    $verificationScript = Join-Path $PSScriptRoot 'verify-package.ps1'
    $verificationParameters = @{
        Version = $Version
    }
    if ($SkipInstaller) {
        $verificationParameters['SkipInstaller'] = $true
    }
    & $verificationScript @verificationParameters

    Write-Host "Release artifacts: $releaseRoot"
}
finally {
    Pop-Location
}
