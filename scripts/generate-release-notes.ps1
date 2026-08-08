<#
.SYNOPSIS
Writes the release notes for a tag from the pull requests merged since the previous tag.

.DESCRIPTION
The repository squash-merges, so every commit subject on main is a pull request title with `(#123)`
appended. That makes `git log` the source of the notes and keeps this offline: no API call, and the same
output whether it runs on a runner or on a workstation.

Commits that carry no pull request number are listed separately rather than dropped. Something that
reached main outside a pull request is exactly what a reader of the notes should be told about.

The output is deliberately plain and complete rather than curated. It is the raw material for a human
editing a draft release before publishing it, which is the only way a release ever gets published.

.PARAMETER Tag
The tag being released, including the `v` prefix.

.PARAMETER PreviousTag
The tag to compare against. Defaults to the nearest tag reachable from Tag's first parent; when there is
none, the notes cover the whole history.

.PARAMETER Repository
`owner/name`, used for the compare link. Defaults to GITHUB_REPOSITORY, then to the origin remote.

.PARAMETER OutputPath
Where to write the notes. Without it they go to standard output.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Tag,
    [string]$PreviousTag,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$AllowFailure)

    # Windows PowerShell turns a native command's redirected stderr into an ErrorRecord, so with
    # $ErrorActionPreference = 'Stop' a git command that writes to stderr throws before its exit code can
    # be inspected — and `git describe` writing "no tags can describe" is a normal answer here, not a
    # failure. Relaxing the preference for the call is what lets the exit code be the signal.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git @Arguments 2>$null
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    if ($LASTEXITCODE -ne 0) {
        if ($AllowFailure) {
            return $null
        }

        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }

    return $output
}

if (-not (Invoke-Git -Arguments @('rev-parse', '--verify', "$Tag^{commit}") -AllowFailure)) {
    throw "$Tag does not exist in this repository. Fetch tags first (git fetch --tags)."
}

if (-not $Repository) {
    $originUrl = Invoke-Git -Arguments @('remote', 'get-url', 'origin') -AllowFailure
    if ($originUrl -match 'github\.com[:/](?<owner>[^/]+)/(?<name>[^/.]+)') {
        $Repository = "$($Matches.owner)/$($Matches.name)"
    }
}

if (-not $PreviousTag) {
    # --first-parent: on a repository that merges rather than squashes, the nearest tag on a side branch
    # is not the previous release.
    $PreviousTag = Invoke-Git -Arguments @('describe', '--tags', '--abbrev=0', '--first-parent', "$Tag^") -AllowFailure
}

$range = if ($PreviousTag) { "$PreviousTag..$Tag" } else { $Tag }
$subjects = @(Invoke-Git -Arguments @('log', '--no-merges', '--reverse', '--pretty=format:%s', $range))
$subjects = @($subjects | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

$pullRequests = @($subjects | Where-Object { $_ -match '\(#\d+\)\s*$' })
$others = @($subjects | Where-Object { $_ -notmatch '\(#\d+\)\s*$' })

$version = $Tag.TrimStart('v')
$lines = [System.Collections.Generic.List[string]]::new()

if ($PreviousTag) {
    $lines.Add("RemoteFlow $version, covering everything merged since $PreviousTag.")
}
else {
    $lines.Add("RemoteFlow $version, the first tagged release.")
}
$lines.Add('')

$lines.Add("## What's changed")
$lines.Add('')
if ($pullRequests.Count -gt 0) {
    foreach ($subject in $pullRequests) {
        $lines.Add("- $subject")
    }
}
else {
    $lines.Add('- No pull requests were merged in this range.')
}
$lines.Add('')

if ($others.Count -gt 0) {
    $lines.Add('## Landed without a pull request')
    $lines.Add('')
    foreach ($subject in $others) {
        $lines.Add("- $subject")
    }
    $lines.Add('')
}

$lines.Add('## Downloads')
$lines.Add('')
$lines.Add("Four Windows artefacts, named for the version and the runtime identifier (``win-x64`` or ``win-arm64``):")
$lines.Add('')
$lines.Add("| File | What it is |")
$lines.Add("| --- | --- |")
$lines.Add("| ``RemoteFlow-$version-win-x64.zip`` | Portable build for Intel and AMD machines. |")
$lines.Add("| ``RemoteFlow-$version-win-arm64.zip`` | Portable build for ARM machines. |")
$lines.Add("| ``RemoteFlow-$version-win-x64-setup.exe`` | Per-user installer for x64. |")
$lines.Add("| ``RemoteFlow-$version-win-arm64-setup.exe`` | Per-user installer for ARM64. |")
$lines.Add('')
$lines.Add('The zips are self-contained: they run on a clean Windows with no .NET runtime installed. The')
$lines.Add('installers are per-user, need no elevation, and leave your connections and settings in place when')
$lines.Add('you uninstall unless you ask for them to be removed.')
$lines.Add('')

$lines.Add('## Verifying your download')
$lines.Add('')
$lines.Add('`checksums.txt` lists the SHA-256 of every file above.')
$lines.Add('')
$lines.Add('```shell')
$lines.Add('sha256sum --check --ignore-missing checksums.txt')
$lines.Add('```')
$lines.Add('')
$lines.Add('On Windows without `sha256sum`:')
$lines.Add('')
$lines.Add('```powershell')
$lines.Add("Get-FileHash .\RemoteFlow-$version-win-x64.zip -Algorithm SHA256")
$lines.Add('```')
$lines.Add('')
$lines.Add('These builds are **not code-signed**, so Windows SmartScreen will warn that the publisher is')
$lines.Add('unknown. That is expected and is not a sign of a corrupted download; the checksum is how you tell')
$lines.Add('the difference.')
$lines.Add('')

if ($Repository) {
    $lines.Add('---')
    $lines.Add('')
    if ($PreviousTag) {
        $lines.Add("**Full changelog**: https://github.com/$Repository/compare/$PreviousTag...$Tag")
    }
    else {
        $lines.Add("**Full changelog**: https://github.com/$Repository/commits/$Tag")
    }
}

$notes = ($lines -join "`n").TrimEnd() + "`n"

if ($OutputPath) {
    $directory = Split-Path -Parent $OutputPath
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    # UTF-8 without a BOM: the notes go straight into a GitHub release body.
    [System.IO.File]::WriteAllText($OutputPath, $notes, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Wrote release notes for $Tag to $OutputPath ($($pullRequests.Count) pull requests, $($others.Count) direct commits)."
}
else {
    Write-Output $notes
}
