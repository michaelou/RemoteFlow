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

.PARAMETER IncludeUpgrade
Also run the installer again over the first install, twice, to cover the parts of an in-app update that a
single install cannot reach: that a silent run with no /DIR finds the previous install through its HKCU
uninstall entry and lands there rather than in the default location, and that the relaunch [Run] entry
fires only when /UPDATE is passed.

CI builds one version per run, so "N+1 replaced N" is not provable here and stays in the manual release
checklist. What is provable is everything an upgrade depends on other than the version number.

Implies -IncludeInstaller, and inherits its side effects.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Runtime,
    [string]$ArtifactDirectory,
    [switch]$IncludeInstaller,
    [switch]$IncludeUpgrade
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if ($IncludeUpgrade) {
    $IncludeInstaller = $true
}

# The AppId from build/windows/RemoteFlow.iss, with the _is1 suffix Inno appends. This is where Setup
# records where it installed, and where it reads that back from when an upgrade runs with no /DIR.
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{6A084A9C-3CFB-4C8F-A7A8-AA5B34D9C91F}_is1'

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

        if ($IncludeUpgrade) {
            # An upgrade is not a fresh install. What makes it land in the right place is a registry value
            # the first run wrote, and nothing above touches it.
            $recordedPath = (Get-ItemProperty -LiteralPath $uninstallKey -Name 'Inno Setup: App Path').'Inno Setup: App Path'
            if ($recordedPath.TrimEnd('\') -ine $installDirectory.TrimEnd('\')) {
                throw "Setup recorded its location as '$recordedPath', not $installDirectory. An in-app update runs with no /DIR and depends on this value."
            }

            # Deleting the binary is the unambiguous assertion: only a run that resolved the previous
            # install directory can put it back. Comparing timestamps would not do, because Inno preserves
            # the source file's write time and an overwritten file looks untouched.
            Remove-Item -LiteralPath (Join-Path $installDirectory 'RemoteFlow.exe') -Force

            # Compared before and after rather than merely tested afterwards: a workstation running this by
            # hand may already have RemoteFlow installed in the default location, and the question is
            # whether this run put something there, not whether anything is there.
            $defaultExe = Join-Path $env:LOCALAPPDATA 'Programs\RemoteFlow\RemoteFlow.exe'
            $defaultExisted = Test-Path -LiteralPath $defaultExe

            $upgradeLog = Join-Path $ArtifactDirectory "upgrade-$Runtime.log"
            $process = Start-Process -FilePath $installerPath `
                -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$upgradeLog" `
                -Wait -PassThru
            if ($process.ExitCode -ne 0) {
                throw "The $Runtime installer exited with code $($process.ExitCode) on the upgrade run. See $upgradeLog."
            }

            if (-not $defaultExisted -and (Test-Path -LiteralPath $defaultExe)) {
                throw "The upgrade run installed into the default location instead of reusing $installDirectory."
            }

            $reported = Assert-VersionOutput `
                -ExePath (Join-Path $installDirectory 'RemoteFlow.exe') `
                -Description "The $Runtime binary restored by the upgrade run"

            # Without /UPDATE nothing may be launched, or a silent deployment puts a window on somebody's
            # screen. Inno logs a "-- Run entry --" header for every [Run] entry it processes.
            if (Select-String -LiteralPath $upgradeLog -Pattern '-- Run entry --' -Quiet) {
                throw "The upgrade run processed a [Run] entry without /UPDATE. See $upgradeLog."
            }

            Write-Host "upgrade   no /DIR -> reused $installDirectory, no relaunch -> $reported"

            # And with /UPDATE it must, or an in-app update leaves the user with no application on screen.
            #
            # Deliberately not -Wait: this run relaunches RemoteFlow, and Start-Process -Wait waits for the
            # whole process tree, so it would block until somebody closed the window. WaitForExit on the
            # returned object waits for Setup alone, which is what "did the install finish" means here.
            $relaunchLog = Join-Path $ArtifactDirectory "relaunch-$Runtime.log"
            $process = Start-Process -FilePath $installerPath `
                -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/UPDATE', "/LOG=$relaunchLog" `
                -PassThru
            if (-not $process.WaitForExit(300000)) {
                throw "The $Runtime installer did not finish within five minutes on the /UPDATE run. See $relaunchLog."
            }

            if ($process.ExitCode -ne 0) {
                throw "The $Runtime installer exited with code $($process.ExitCode) on the /UPDATE run. See $relaunchLog."
            }

            # The relaunched application starts after Setup exits, so give it a moment to appear in the log
            # and on the process list before asserting and before the finally block closes it.
            Start-Sleep -Seconds 3

            # The log records the entry before CreateProcess is called, so this asserts the [Run] entry was
            # selected -- the part under test -- without depending on a window surviving on a build runner.
            $expectedRelaunch = [regex]::Escape((Join-Path $installDirectory 'RemoteFlow.exe'))
            if (-not (Select-String -LiteralPath $relaunchLog -Pattern $expectedRelaunch -Quiet)) {
                throw "The /UPDATE run did not launch $installDirectory\RemoteFlow.exe. See $relaunchLog."
            }

            Write-Host "relaunch  /UPDATE -> launched $installDirectory\RemoteFlow.exe"
            $results.Add([pscustomobject]@{ Artefact = 'upgrade + relaunch'; Reported = $reported })
        }
    }
    finally {
        # The /UPDATE run above starts RemoteFlow, and AppMutex means the uninstaller refuses while it is
        # running. Closing it is what a user does by closing the window.
        Stop-Process -Name 'RemoteFlow' -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2

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
