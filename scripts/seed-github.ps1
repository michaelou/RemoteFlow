<#
.SYNOPSIS
    Seeds the RemoteFlow GitHub repository with labels, milestones and the v1 issue backlog.

.DESCRIPTION
    Idempotent. Safe to re-run:
      * labels use `gh label create --force` (create-or-update)
      * milestones are created only when the title is absent
      * issues are created only when no open-or-closed issue already has that exact title

    Issues are created in ascending Number order so that the GitHub issue numbers match the
    `depends_on` / `blocks` references inside the issue bodies. Do not reorder the data files.

.PARAMETER Repo
    owner/name. Defaults to michaelou/RemoteFlow.

.PARAMETER SkipLabels
.PARAMETER SkipMilestones
.PARAMETER SkipIssues
    Skip individual phases.

.PARAMETER WhatIf
    Report what would be created without calling the API.

.EXAMPLE
    ./scripts/seed-github.ps1
    ./scripts/seed-github.ps1 -WhatIf
    ./scripts/seed-github.ps1 -SkipLabels -SkipMilestones
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Repo = 'michaelou/RemoteFlow',
    [switch] $SkipLabels,
    [switch] $SkipMilestones,
    [switch] $SkipIssues
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Write-Step   { param($m) Write-Host "`n=== $m" -ForegroundColor Cyan }
function Write-Ok     { param($m) Write-Host "  [ok]   $m" -ForegroundColor Green }
function Write-Skip   { param($m) Write-Host "  [skip] $m" -ForegroundColor DarkGray }
function Write-Warn2  { param($m) Write-Host "  [warn] $m" -ForegroundColor Yellow }

# ---------------------------------------------------------------- preflight

Write-Step "Preflight"

$ghPath = (Get-Command gh -ErrorAction SilentlyContinue)
if ($null -eq $ghPath) { throw "gh CLI not found on PATH. Install from https://cli.github.com/" }
Write-Ok "gh found: $((gh --version | Select-Object -First 1))"

gh auth status 2>$null | Out-Null
if (-not $?) { throw "gh is not authenticated. Run: gh auth login" }
Write-Ok "gh authenticated"

$repoJson = gh repo view $Repo --json name,owner,hasIssuesEnabled | ConvertFrom-Json
if (-not $repoJson.hasIssuesEnabled) { throw "Issues are disabled on $Repo. Enable them first." }
Write-Ok "repo reachable: $Repo"

# ---------------------------------------------------------------- labels

$defaultLabels = @(
    'bug', 'documentation', 'duplicate', 'enhancement', 'good first issue',
    'help wanted', 'invalid', 'question', 'wontfix'
)

# Ordered so the list reads sensibly in the GitHub UI.
$labels = @(
    @{ Name = 'model:opus-5';      Color = '6E40C9'; Description = 'Run with claude-opus-5 - load-bearing, security-sensitive, undocumented API, or tricky async' }
    @{ Name = 'model:sonnet-5';    Color = 'A371F7'; Description = 'Run with claude-sonnet-5 - the default tier' }
    @{ Name = 'model:haiku-4.5';   Color = 'D8B9FF'; Description = 'Run with claude-haiku-4-5 - mechanical, small read-set. No effort label applies (the parameter 400s)' }

    @{ Name = 'effort:low';        Color = 'BFE5B3'; Description = 'output_config.effort=low - the plan is in the issue; transcription' }
    @{ Name = 'effort:medium';     Color = 'FBCA04'; Description = 'effort=medium - one or two local decisions, an in-repo pattern exists' }
    @{ Name = 'effort:high';       Color = 'D93F0B'; Description = 'effort=high - interacting choices, or failure would be silent' }
    @{ Name = 'effort:xhigh';      Color = 'B60205'; Description = 'effort=xhigh - long-horizon multi-file, or undocumented-API exploration' }
    @{ Name = 'effort:max';        Color = '4C0000'; Description = 'effort=max - re-runs only; cap at 2 per backlog. Never a first attempt' }

    @{ Name = 'area:core';         Color = '0366D6'; Description = 'Domain and Application layers' }
    @{ Name = 'area:data';         Color = '0366D6'; Description = 'EF Core, SQLite, repositories, queries' }
    @{ Name = 'area:ui';           Color = '0366D6'; Description = 'Avalonia views, view models, theming' }
    @{ Name = 'area:terminal';     Color = '0366D6'; Description = 'Terminal control, PTY, keymap, clipboard' }
    @{ Name = 'area:ssh';          Color = '0366D6'; Description = 'SSH transport, auth, sessions' }
    @{ Name = 'area:sftp';         Color = '0366D6'; Description = 'SFTP browsing, transfers, remote editing' }
    @{ Name = 'area:rdp';          Color = '0366D6'; Description = 'RDP launchers and options' }
    @{ Name = 'area:platform';     Color = '0366D6'; Description = 'Per-OS integration: paths, launchers, P/Invoke' }
    @{ Name = 'area:backup';       Color = '0366D6'; Description = 'Export, import, merge, archive format' }
    @{ Name = 'area:build';        Color = '0366D6'; Description = 'Solution layout, packaging, CI, docs tooling' }
    @{ Name = 'area:security';     Color = '000000'; Description = 'Credentials, crypto, host key trust - read the diff carefully' }

    @{ Name = 'type:feature';      Color = 'EDEDED'; Description = 'New user-facing or service capability' }
    @{ Name = 'type:infra';        Color = 'EDEDED'; Description = 'Build, tooling, packaging, wiring' }
    @{ Name = 'type:docs';         Color = 'EDEDED'; Description = 'Documentation and decision records' }
    @{ Name = 'type:test';         Color = 'EDEDED'; Description = 'Test harnesses and coverage' }
    @{ Name = 'type:spike';        Color = 'FEF2C0'; Description = 'Time-boxed investigation with an unknown outcome' }

    @{ Name = 'risk:load-bearing'; Color = 'D4327E'; Description = 'Decisions propagate to later issues; read the diff yourself before merging' }
    @{ Name = 'risk:contained';    Color = 'F7C6E0'; Description = 'A mistake is cheap and locally fixable' }
    @{ Name = 'blocked';           Color = '6A737D'; Description = 'Waiting on a dependency issue' }
    @{ Name = 'stretch';           Color = 'E1E4E8'; Description = 'Out of v1 must-have scope' }
)

if (-not $SkipLabels) {
    Write-Step "Labels"

    $existing = (gh label list -R $Repo --limit 200 --json name | ConvertFrom-Json).name
    foreach ($d in $defaultLabels) {
        if ($existing -contains $d) {
            if ($PSCmdlet.ShouldProcess("label '$d'", 'delete')) {
                gh label delete $d -R $Repo --yes 2>$null | Out-Null
                Write-Ok "deleted default label '$d'"
            }
        }
    }

    foreach ($l in $labels) {
        if ($PSCmdlet.ShouldProcess("label '$($l.Name)'", 'create/update')) {
            gh label create $l.Name -R $Repo --color $l.Color --description $l.Description --force | Out-Null
            if ($?) { Write-Ok $l.Name } else { Write-Warn2 "failed: $($l.Name)" }
        }
    }
}

# ---------------------------------------------------------------- milestones

$milestones = @(
    @{ Title = '1 - Foundation';             Description = 'Repo governance, solution skeleton, domain model, persistence, app shell and DI. The terminal spike runs here in parallel.' }
    @{ Title = '2 - Connection Management';  Description = 'Secure credential storage per platform, CRUD/folders/tags/search, explorer and editor UI.' }
    @{ Title = '3 - Embedded Terminal';      Description = 'ITerminalChannel, local PTY, terminal control host, tabs, keymap, clipboard, resize and throughput.' }
    @{ Title = '4 - SSH';                    Description = 'ISshTransport, Tmds.Ssh, host key verification and trust UI, auth flows, sessions, reconnect.' }
    @{ Title = '5 - SFTP';                   Description = 'Browse, transfer engine, file operations, permissions, remote editing with conflict detection.' }
    @{ Title = '6 - Remote Desktop';         Description = 'Platform-native RDP launchers and options UI. No embedded RDP in v1.' }
    @{ Title = '7 - Backup and Restore';     Description = 'Versioned archive format, export/import, merge/replace, encrypted credential export.' }
    @{ Title = '8 - Packaging and Release';  Description = 'The first CI in the repo. Versioning, Windows packaging, release workflow, accessibility and docs.' }
)

if (-not $SkipMilestones) {
    Write-Step "Milestones"

    $existingMs = gh api "repos/$Repo/milestones?state=all&per_page=100" | ConvertFrom-Json
    $existingTitles = @($existingMs | ForEach-Object { $_.title })

    foreach ($m in $milestones) {
        if ($existingTitles -contains $m.Title) {
            Write-Skip "$($m.Title) (exists)"
            continue
        }
        if ($PSCmdlet.ShouldProcess("milestone '$($m.Title)'", 'create')) {
            gh api "repos/$Repo/milestones" -X POST -f "title=$($m.Title)" -f "description=$($m.Description)" --silent
            if ($?) { Write-Ok $m.Title } else { Write-Warn2 "failed: $($m.Title)" }
        }
    }
}

# ---------------------------------------------------------------- issues

if (-not $SkipIssues) {
    Write-Step "Issues"

    $dataFiles = @('m1.ps1', 'm2.ps1', 'm3.ps1', 'm4.ps1', 'm5.ps1', 'm6-m8.ps1')
    $issues = @()
    foreach ($f in $dataFiles) {
        $path = Join-Path $scriptRoot "issues/$f"
        if (-not (Test-Path $path)) { throw "Missing issue data file: $path" }
        $issues += & $path
    }

    # Script-block sort is required: `Sort-Object Number` silently does nothing on [hashtable]
    # in PowerShell 5.1 (property lookup sees Count/Keys/Values, not the entry keys), which would
    # create the issues out of order and misalign every depends_on / blocks reference.
    $issues = $issues | Sort-Object -Property { [int]$_.Number }
    Write-Ok "loaded $($issues.Count) issue definitions"

    # Validate before touching the API - a bad backlog is cheaper to catch here.
    $numbers = $issues | ForEach-Object { $_.Number }
    $dupes = $numbers | Group-Object | Where-Object { $_.Count -gt 1 }
    if ($dupes) { throw "Duplicate issue numbers: $($dupes.Name -join ', ')" }

    $expected = 1..$issues.Count
    $missing = $expected | Where-Object { $numbers -notcontains $_ }
    if ($missing) { throw "Issue numbers are not contiguous from 1. Missing: $($missing -join ', ')" }

    $validLabels = @($labels | ForEach-Object { $_.Name })
    $validMilestones = @($milestones | ForEach-Object { $_.Title })
    foreach ($i in $issues) {
        foreach ($l in $i.Labels) {
            if ($validLabels -notcontains $l) { throw "Issue $($i.Number) uses unknown label '$l'" }
        }
        if ($validMilestones -notcontains $i.Milestone) { throw "Issue $($i.Number) uses unknown milestone '$($i.Milestone)'" }

        $hasHaiku  = $i.Labels -contains 'model:haiku-4.5'
        $hasEffort = @($i.Labels | Where-Object { $_ -like 'effort:*' }).Count -gt 0
        if ($hasHaiku -and $hasEffort) { throw "Issue $($i.Number) is model:haiku-4.5 but carries an effort label - the API rejects output_config.effort on Haiku 4.5" }
        if (-not $hasHaiku -and -not $hasEffort) { throw "Issue $($i.Number) has no effort label and is not a Haiku issue" }

        $modelCount = @($i.Labels | Where-Object { $_ -like 'model:*' }).Count
        if ($modelCount -ne 1) { throw "Issue $($i.Number) must carry exactly one model:* label (found $modelCount)" }
    }
    Write-Ok "validated: numbering contiguous, labels/milestones known, model+effort pairing correct"

    $existingIssues = gh issue list -R $Repo --state all --limit 300 --json number,title | ConvertFrom-Json
    $existingIssueTitles = @($existingIssues | ForEach-Object { $_.title })

    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "remoteflow-seed-$PID"
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

    try {
        foreach ($i in $issues) {
            if ($existingIssueTitles -contains $i.Title) {
                Write-Skip "#$($i.Number) $($i.Title) (exists)"
                continue
            }

            if (-not $PSCmdlet.ShouldProcess("issue #$($i.Number) '$($i.Title)'", 'create')) { continue }

            $bodyFile = Join-Path $tempDir "$($i.Number).md"
            # UTF-8 without BOM: gh sends the file verbatim and a BOM would show in the rendered body.
            [System.IO.File]::WriteAllText($bodyFile, $i.Body, (New-Object System.Text.UTF8Encoding($false)))

            $labelArgs = @()
            foreach ($l in $i.Labels) { $labelArgs += '--label'; $labelArgs += $l }

            $url = gh issue create -R $Repo --title $i.Title --body-file $bodyFile --milestone $i.Milestone @labelArgs
            if ($?) { Write-Ok "#$($i.Number) $($i.Title)" } else { Write-Warn2 "failed: #$($i.Number) $($i.Title)" }
        }
    }
    finally {
        Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
    }
}

Write-Step "Done"
Write-Host "  Verify with:" -ForegroundColor DarkGray
Write-Host "    gh label list -R $Repo --limit 100" -ForegroundColor DarkGray
Write-Host "    gh api repos/$Repo/milestones --jq '.[].title'" -ForegroundColor DarkGray
Write-Host "    gh issue list -R $Repo --limit 100 --json number,title,labels,milestone" -ForegroundColor DarkGray
