<#
.SYNOPSIS
Proves that the artefacts in artifacts/ are the ones a release should carry: named for the expected
version, and containing a binary that actually starts.

.DESCRIPTION
publish-windows.ps1 smoke-tests the publish directory before packaging. This tests what packaging
produced, which is what users download — a zip that lost a file, or an installer compiled against a
stale source directory, both survive the earlier check and fail here.

Run from anywhere; paths resolve from the script's own location.

Only the matching architecture can be exercised, so this is invoked once per architecture on a runner of
that architecture rather than twice on one machine.

.PARAMETER ExpectedVersion
The version the artefact names must carry and the binary must report, without a leading `v`. Passing it
in rather than discovering it is the point: it is how a tag and a build that disagree get caught.

.PARAMETER Runtime
Which architecture's artefacts to test. Must match the machine running the script.

.PARAMETER ArtifactDirectory
Where the artefacts are. Defaults to artifacts/ beside the repository root.

.PARAMETER IncludeInstaller
Also install the installer into a temporary directory, run the installed binary, and uninstall.

Off by default because it is not free of side effects: the installer writes the per-user uninstall entry
under HKCU that identifies an existing RemoteFlow install, so running this on a machine with RemoteFlow
installed will point that entry at the temporary directory. CI passes it; on a workstation, only do so if
you do not have RemoteFlow installed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Runtime,
    [string]$ArtifactDirectory,
    [switch]$IncludeInstaller
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $ArtifactDirectory) {
    $ArtifactDirectory = Join-Path $repositoryRoot 'artifacts'
}

$ExpectedVersion = $ExpectedVersion.TrimStart('v')

$machineArchitecture = $env:PROCESSOR_ARCHITECTURE.Replace('AMD64', 'x64')
$artefactArchitecture = $Runtime -replace '^win-', ''
if ($artefactArchitecture -ine $machineArchitecture) {
    # Silently skipping would turn "the binary is broken" into a green run, which is the failure this
    # whole script exists to prevent.
    throw "Cannot smoke-test $Runtime artefacts on a $machineArchitecture machine. Run this on a $artefactArchitecture runner."
}

# RemoteFlow.exe is a GUI-subsystem binary. PowerShell neither waits for one nor captures its output when
# invoked with the call operator, so a check written as `$x = & $exe --version` passes without having run
# anything. Start-Process with redirection is what actually observes a WinExe.
function Assert-VersionOutput {
    param(
        [Parameter(Mandatory)][string]$ExePath,
        [Parameter(Mandatory)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $ExePath)) {
        throw "$Description does not contain RemoteFlow.exe at $ExePath."
    }

    $standardOutput = [System.IO.Path]::GetTempFileName()
    $standardError = [System.IO.Path]::GetTempFileName()
    try {
        $process = Start-Process -FilePath $ExePath -ArgumentList '--version' `
            -Wait -PassThru -NoNewWindow `
            -RedirectStandardOutput $standardOutput -RedirectStandardError $standardError
        $errorText = (Get-Content -LiteralPath $standardError -Raw)
        if ($process.ExitCode -ne 0) {
            throw "$Description failed --version with exit code $($process.ExitCode). $errorText"
        }

        $reported = (Get-Content -LiteralPath $standardOutput -Raw)
        if ([string]::IsNullOrWhiteSpace($reported)) {
            throw "$Description printed nothing for --version. $errorText"
        }

        $reported = $reported.Trim()
        if ($reported -notmatch [regex]::Escape($ExpectedVersion)) {
            throw "$Description reported '$reported', which does not contain $ExpectedVersion."
        }

        return $reported
    }
    finally {
        Remove-Item -LiteralPath $standardOutput, $standardError -Force -ErrorAction SilentlyContinue
    }
}

# Windows can hold an image lock for a moment after a process exits, and these directories are deleted
# immediately after the binary inside them has run.
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

function New-TemporaryDirectory {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("remoteflow-smoke-" + [guid]::NewGuid().ToString('n'))
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}

$results = [System.Collections.Generic.List[object]]::new()

# --- The portable zip ---------------------------------------------------------------------------------

$zipPath = Join-Path $ArtifactDirectory "RemoteFlow-$ExpectedVersion-$Runtime.zip"
if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Expected $zipPath. Either the build produced a different version than $ExpectedVersion, or the artefact is missing."
}

$extractDirectory = New-TemporaryDirectory
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $extractDirectory)
    $reported = Assert-VersionOutput `
        -ExePath (Join-Path $extractDirectory 'RemoteFlow.exe') `
        -Description "The $Runtime portable zip"
    Write-Host "zip       $(Split-Path -Leaf $zipPath) -> $reported"
    $results.Add([pscustomobject]@{ Artefact = Split-Path -Leaf $zipPath; Reported = $reported })
}
finally {
    Remove-DirectoryWithRetry -Path $extractDirectory
}

# --- The installer ------------------------------------------------------------------------------------

$installerPath = Join-Path $ArtifactDirectory "RemoteFlow-$ExpectedVersion-$Runtime-setup.exe"
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Expected $installerPath. Either the build produced a different version than $ExpectedVersion, or the installer is missing."
}

if (-not $IncludeInstaller) {
    Write-Host "installer $(Split-Path -Leaf $installerPath) -> present, not run (-IncludeInstaller not passed)"
}
else {
    $installDirectory = New-TemporaryDirectory
    try {
        $process = Start-Process -FilePath $installerPath `
            -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', "/DIR=$installDirectory" `
            -Wait -PassThru
        if ($process.ExitCode -ne 0) {
            throw "The $Runtime installer exited with code $($process.ExitCode)."
        }

        $reported = Assert-VersionOutput `
            -ExePath (Join-Path $installDirectory 'RemoteFlow.exe') `
            -Description "The $Runtime installed binary"
        Write-Host "installer $(Split-Path -Leaf $installerPath) -> $reported"
        $results.Add([pscustomobject]@{ Artefact = Split-Path -Leaf $installerPath; Reported = $reported })
    }
    finally {
        # Best effort. Inno's uninstaller relaunches itself from a temporary copy and returns before the
        # work is done, so a failure here says nothing reliable about the artefact; the release checklist
        # in docs/packaging-windows.md covers uninstall behaviour properly, by hand.
        $uninstaller = Join-Path $installDirectory 'unins000.exe'
        if (Test-Path -LiteralPath $uninstaller) {
            try {
                Start-Process -FilePath $uninstaller `
                    -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' `
                    -Wait -PassThru | Out-Null
                Start-Sleep -Seconds 2
            }
            catch {
                Write-Warning "Silent uninstall of the smoke-test install failed: $_"
            }
        }

        Remove-Item -LiteralPath $installDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host "All $Runtime artefacts report $ExpectedVersion." -ForegroundColor Green
$results | Format-Table -AutoSize
