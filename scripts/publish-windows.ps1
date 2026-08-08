<#
.SYNOPSIS
Builds the Windows release artefacts: a self-contained publish per architecture, a portable zip, and an
Inno Setup installer.

.DESCRIPTION
Run from anywhere; paths are resolved from the script's own location. Output lands in artifacts/ which is
git-ignored.

Self-contained means the zip runs on a Windows machine with no .NET installed. Trimming and AOT stay off:
EF Core and Avalonia's XAML loader rely on reflection a trimmer cannot follow.

The installer step is skipped with a warning when Inno Setup is not installed, so the zip can still be
produced on a machine without it. Signing is delegated to sign-windows.ps1, which no-ops when no
certificate is configured.

.PARAMETER Runtime
Which architectures to build. Both by default.

.PARAMETER SkipInstaller
Produce only the portable zip.

.PARAMETER KeepPublishOutput
Leave the intermediate publish directories in place for inspection.
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]]$Runtime = @('win-x64', 'win-arm64'),
    [string]$Configuration = 'Release',
    [string]$OutputDirectory,
    [switch]$SkipInstaller,
    [switch]$KeepPublishOutput,
    [string]$CertificateThumbprint = $env:REMOTEFLOW_SIGN_THUMBPRINT,
    [string]$TimestampUrl = $env:REMOTEFLOW_SIGN_TIMESTAMP_URL
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src/RemoteFlow.Desktop/RemoteFlow.Desktop.csproj'
$installerScript = Join-Path $repositoryRoot 'build/windows/RemoteFlow.iss'
$signScript = Join-Path $PSScriptRoot 'sign-windows.ps1'

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts'
}
$publishRoot = Join-Path $OutputDirectory 'publish'
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

# Windows can hold an image lock for a moment after a process exits, and the smoke test runs the binary
# out of the directory this then deletes. Retrying is the difference between a reliable script and one
# that fails every second run for no reason the reader can see.
function Remove-DirectoryWithRetry {
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$Attempts = 5
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -eq $Attempts) {
                throw
            }

            Start-Sleep -Milliseconds (200 * $attempt)
        }
    }
}

# RemoteFlow.exe is a GUI-subsystem binary, and PowerShell neither waits for one nor captures its output
# when invoked with the call operator: `$x = & $exe --version` yields nothing and leaves $LASTEXITCODE
# unset, so a check written that way passes without ever having run. Start-Process with redirection is
# what actually observes a WinExe.
function Invoke-VersionSmokeTest {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$Runtime
    )

    $standardOutput = [System.IO.Path]::GetTempFileName()
    $standardError = [System.IO.Path]::GetTempFileName()
    try {
        $process = Start-Process -FilePath $ExePath -ArgumentList '--version' `
            -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $standardOutput -RedirectStandardError $standardError
        $errorText = (Get-Content -LiteralPath $standardError -Raw)
        if ($process.ExitCode -ne 0) {
            throw "The published $Runtime binary failed --version with exit code $($process.ExitCode). $errorText"
        }

        $reported = (Get-Content -LiteralPath $standardOutput -Raw)
        if ([string]::IsNullOrWhiteSpace($reported)) {
            throw "The published $Runtime binary printed nothing for --version. $errorText"
        }

        $reported = $reported.Trim()
        if ($reported -notmatch [regex]::Escape($ExpectedVersion)) {
            throw "The published $Runtime binary reported '$reported', which does not contain $ExpectedVersion."
        }

        return $reported
    }
    finally {
        Remove-Item -LiteralPath $standardOutput, $standardError -Force -ErrorAction SilentlyContinue
    }
}

function Resolve-InnoSetupCompiler {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    # Inno Setup installs per-user by default, so %LOCALAPPDATA%\Programs is at least as likely as either
    # Program Files directory.
    $candidates = @(
            (Join-Path $env:LOCALAPPDATA 'Programs'),
            ${env:ProgramFiles(x86)},
            $env:ProgramFiles
        ) |
        Where-Object { $_ } |
        ForEach-Object { Join-Path $_ 'Inno Setup 6\ISCC.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } |
        Select-Object -First 1
    # Select-Object rather than [0]: a single match arrives as a string, and indexing a string returns its
    # first character.
    if ($candidates) {
        return $candidates
    }

    return $null
}

# The version is whatever the build stamped into the binary, so the artefact names cannot disagree with
# what --version reports. ProductVersion carries the commit as +sha; the artefact name does not need it.
function Get-PublishedVersion {
    param([Parameter(Mandatory)][string]$ExePath)

    $productVersion = (Get-Item -LiteralPath $ExePath).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion)) {
        throw "The published executable at $ExePath carries no version. Is MinVer wired in?"
    }

    $plusIndex = $productVersion.IndexOf('+')
    if ($plusIndex -ge 0) {
        return $productVersion.Substring(0, $plusIndex)
    }

    return $productVersion
}

# Inno's VersionInfoVersion wants four numbers; a prerelease version such as 0.0.0-alpha.0.58 is not one.
function Get-FileVersion {
    param([Parameter(Mandatory)][string]$ExePath)

    $fileVersion = (Get-Item -LiteralPath $ExePath).VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($fileVersion)) {
        return '0.0.0.0'
    }

    return $fileVersion
}

$results = [System.Collections.Generic.List[object]]::new()

foreach ($rid in $Runtime) {
    Write-Host ''
    Write-Host "=== $rid ===" -ForegroundColor Cyan

    $publishDirectory = Join-Path $publishRoot $rid
    if (Test-Path -LiteralPath $publishDirectory) {
        # A stale file left behind by an earlier build would be zipped and shipped.
        Remove-DirectoryWithRetry -Path $publishDirectory
    }

    dotnet publish $project `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained true `
        --output $publishDirectory `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $rid with exit code $LASTEXITCODE."
    }

    $exePath = Join-Path $publishDirectory 'RemoteFlow.exe'
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "Publish for $rid did not produce RemoteFlow.exe."
    }

    $version = Get-PublishedVersion -ExePath $exePath
    $fileVersion = Get-FileVersion -ExePath $exePath
    $architecture = $rid -replace '^win-', ''
    Write-Host "Published $version ($architecture)"

    & $signScript -Path @($exePath) -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl

    $zipPath = Join-Path $OutputDirectory "RemoteFlow-$version-$rid.zip"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDirectory,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
    Write-Host "Portable zip: $zipPath"

    $installerPath = $null
    if ($SkipInstaller) {
        Write-Host 'Installer skipped (-SkipInstaller).'
    }
    else {
        $iscc = Resolve-InnoSetupCompiler
        if (-not $iscc) {
            Write-Warning 'Inno Setup 6 was not found, so no installer was built. Install it (winget install --id JRSoftware.InnoSetup) or pass -SkipInstaller to silence this.'
        }
        else {
            $outputBaseName = "RemoteFlow-$version-$rid-setup"
            & $iscc `
                "/DAppVersion=$version" `
                "/DFileVersion=$fileVersion" `
                "/DAppArchitecture=$architecture" `
                "/DSourceDir=$publishDirectory" `
                "/DOutputDir=$OutputDirectory" `
                "/DOutputBaseName=$outputBaseName" `
                "/DRepositoryRoot=$repositoryRoot" `
                $installerScript
            if ($LASTEXITCODE -ne 0) {
                throw "Inno Setup failed for $rid with exit code $LASTEXITCODE."
            }

            $installerPath = Join-Path $OutputDirectory "$outputBaseName.exe"
            & $signScript -Path @($installerPath) -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl
            Write-Host "Installer: $installerPath"
        }
    }

    # A packaging smoke test worth more than its few lines: it proves the published binary starts, finds
    # its runtime, and knows what it is. Only the matching architecture can run here.
    $smokeTest = 'skipped (cross-architecture)'
    if ($architecture -ieq $env:PROCESSOR_ARCHITECTURE.Replace('AMD64', 'x64')) {
        $smokeTest = Invoke-VersionSmokeTest -ExePath $exePath -ExpectedVersion $version -Runtime $rid
    }

    $results.Add([pscustomobject]@{
        Runtime   = $rid
        Version   = $version
        Zip       = $zipPath
        Installer = $installerPath
        Reported  = $smokeTest
    })

    if (-not $KeepPublishOutput) {
        Remove-DirectoryWithRetry -Path $publishDirectory
    }
}

Write-Host ''
$results | Format-List
