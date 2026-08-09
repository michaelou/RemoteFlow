<#
.SYNOPSIS
Regenerates THIRD-PARTY-NOTICES.md and build/licenses/package-licenses.txt from the resolved package
graph of the shipping application.

.DESCRIPTION
The source of truth is what NuGet actually resolved for src/RemoteFlow.Desktop, transitive packages
included — not the list of PackageReference lines someone remembered to write down. A transitive
dependency that appears without anyone choosing it is exactly the one a hand-maintained notices file
misses.

Two files come out of one pass, so they cannot disagree:

- THIRD-PARTY-NOTICES.md, for people. Every shipping package with its version, licence, and copyright,
  followed by the full text of each licence named.
- build/licenses/package-licenses.txt, for the build. A flat `id|version|spdx` manifest that
  build/licenses/PackageLicenses.targets checks on every build of the desktop application, which is what
  turns "a package arrived under a licence nobody approved" into a failed build rather than a discovery
  made later.

A licence this cannot identify is an error, never a guess: `dotnet build` refusing to produce a binary is
the correct response to not knowing what is inside it. Packages whose .nuspec carries only a bundled
licence file get a human-recorded answer in build/licenses/license-overrides.txt.

The output is deterministic — sorted, no timestamps, LF endings — so running this twice produces no diff.

.PARAMETER Verify
Generate into memory and compare with what is on disk. Non-zero exit and a diff summary when they differ.
This is what CI runs; it is how a pull request that adds a dependency without regenerating the notices
gets caught.

.PARAMETER Project
The project whose resolved graph defines "shipping". Defaults to src/RemoteFlow.Desktop.
#>
[CmdletBinding()]
param(
    [switch]$Verify,
    [string]$Project
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $Project) {
    $Project = Join-Path $repositoryRoot 'src/RemoteFlow.Desktop/RemoteFlow.Desktop.csproj'
}

$licenseDirectory = Join-Path $repositoryRoot 'build/licenses'
$noticesPath = Join-Path $repositoryRoot 'THIRD-PARTY-NOTICES.md'
$manifestPath = Join-Path $licenseDirectory 'package-licenses.txt'

# --- Policy files -------------------------------------------------------------------------------------

function Read-PolicyLines {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing policy file: $Path"
    }

    return @(Get-Content -LiteralPath $Path |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') })
}

$allowedLicenses = Read-PolicyLines -Path (Join-Path $licenseDirectory 'allowed-licenses.txt')
$buildOnlyPackages = Read-PolicyLines -Path (Join-Path $licenseDirectory 'build-only-packages.txt')

# <PackageId>=<SPDX>[;<copyright>]. The optional copyright is for packages whose .nuspec names the
# redistributor rather than the author of the code the licence actually covers.
$overrides = @{}
foreach ($line in Read-PolicyLines -Path (Join-Path $licenseDirectory 'license-overrides.txt')) {
    $separator = $line.IndexOf('=')
    if ($separator -lt 1) {
        throw "build/licenses/license-overrides.txt: '$line' is not <PackageId>=<SPDX>[;<copyright>]."
    }

    $id = $line.Substring(0, $separator).Trim()
    $value = $line.Substring($separator + 1)
    $semicolon = $value.IndexOf(';')
    $overrides[$id] = if ($semicolon -ge 0) {
        [pscustomobject]@{
            License   = $value.Substring(0, $semicolon).Trim()
            Copyright = $value.Substring($semicolon + 1).Trim()
        }
    }
    else {
        [pscustomobject]@{ License = $value.Trim(); Copyright = '' }
    }
}

# --- The resolved package graph -----------------------------------------------------------------------

# `dotnet list package` reports what restore resolved rather than what the project files ask for, which is
# the only list that includes the transitive dependencies nobody chose deliberately.
Write-Host "Resolving the package graph for $(Split-Path -Leaf $Project)..."
$listOutput = & dotnet list $Project package --include-transitive --format json
if ($LASTEXITCODE -ne 0) {
    throw "dotnet list package failed with exit code $LASTEXITCODE. Run dotnet restore first."
}

$graph = ($listOutput | Out-String) | ConvertFrom-Json
$resolved = @{}
# Not $project: PowerShell variables are case-insensitive, and assigning to it would go through the
# [string] $Project parameter's type constraint and quietly stringify the object.
foreach ($graphProject in $graph.projects) {
    foreach ($framework in $graphProject.frameworks) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if ($package -and $package.id) {
                $resolved[$package.id] = $package.resolvedVersion
            }
        }
    }
}

if ($resolved.Count -eq 0) {
    throw 'The package graph came back empty. Run dotnet restore first.'
}

# --- Where the packages live --------------------------------------------------------------------------

function Get-PackagesRoot {
    if ($env:NUGET_PACKAGES) {
        return $env:NUGET_PACKAGES
    }

    $listed = & dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -eq 0) {
        foreach ($line in @($listed)) {
            $separator = $line.IndexOf(':')
            # "global-packages: C:\Users\...": the colon after the drive letter is not the separator.
            if ($separator -ge 0) {
                $path = $line.Substring($separator + 1).Trim()
                if ($path -and (Test-Path -LiteralPath $path)) {
                    return $path
                }
            }
        }
    }

    return Join-Path $HOME '.nuget/packages'
}

$packagesRoot = Get-PackagesRoot
if (-not (Test-Path -LiteralPath $packagesRoot)) {
    throw "The NuGet package folder $packagesRoot does not exist. Run dotnet restore first."
}

# --- Reading one package ------------------------------------------------------------------------------

function Get-PackageMetadata {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Version
    )

    $lowered = $Id.ToLowerInvariant()
    $directory = Join-Path $packagesRoot (Join-Path $lowered $Version)
    $nuspec = Join-Path $directory "$lowered.nuspec"
    if (-not (Test-Path -LiteralPath $nuspec)) {
        throw "No .nuspec for $Id $Version at $nuspec. Run dotnet restore first."
    }

    [xml]$document = Get-Content -LiteralPath $nuspec -Raw
    $metadata = $document.package.metadata

    # The nuspec's own <id> preserves the author's casing; the folder name does not.
    $canonicalId = if ($metadata.id) { [string]$metadata.id } else { $Id }

    $license = $null
    $licenseSource = $null
    $overriddenCopyright = ''
    if ($overrides.ContainsKey($canonicalId)) {
        $license = $overrides[$canonicalId].License
        $overriddenCopyright = $overrides[$canonicalId].Copyright
        $licenseSource = 'override'
    }
    elseif ($metadata.license -and $metadata.license.type -eq 'expression') {
        $license = [string]$metadata.license.'#text'
        $licenseSource = 'nuspec'
    }

    if (-not $license) {
        $carried = if ($metadata.license) {
            "a licence of type '$($metadata.license.type)'"
        }
        elseif ($metadata.licenseUrl) {
            "only the URL $($metadata.licenseUrl)"
        }
        else {
            'no licence information at all'
        }

        throw @"
$canonicalId $Version carries $carried, so its licence cannot be identified automatically.

Read the licence shipped in $directory, then record what it is in build/licenses/license-overrides.txt
with a comment saying where you read it. Do not guess.
"@
    }

    if ($allowedLicenses -notcontains $license) {
        throw @"
$canonicalId $Version is licensed $license, which is not in build/licenses/allowed-licenses.txt.

RemoteFlow is MIT. Shipping this package means either accepting $license deliberately — add it to the
allowed list with a reason, and add build/licenses/texts/$license.txt — or removing the dependency.
"@
    }

    # <copyright> is optional and frequently absent; the authors are the next best attribution, and the
    # licence texts below carry the terms either way.
    $copyright = if ($overriddenCopyright) {
        $overriddenCopyright
    }
    elseif ($metadata.copyright) {
        ([string]$metadata.copyright).Trim()
    }
    else {
        ''
    }

    if (-not $copyright -and $metadata.authors) {
        $copyright = ([string]$metadata.authors).Trim()
    }

    $projectUrl = if ($metadata.projectUrl) { ([string]$metadata.projectUrl).Trim() } else { '' }
    if (-not $projectUrl -and $metadata.repository -and $metadata.repository.url) {
        $projectUrl = ([string]$metadata.repository.url).Trim()
    }

    return [pscustomobject]@{
        Id            = $canonicalId
        Version       = $Version
        License       = $license
        LicenseSource = $licenseSource
        Copyright     = ($copyright -replace '\s+', ' ')
        ProjectUrl    = $projectUrl
        BuildOnly     = $buildOnlyPackages -contains $canonicalId
    }
}

$packages = @(
    $resolved.Keys |
        Sort-Object -Property { $_ } |
        ForEach-Object { Get-PackageMetadata -Id $_ -Version $resolved[$_] } |
        Sort-Object -Property Id
)

$shipping = @($packages | Where-Object { -not $_.BuildOnly })
if ($shipping.Count -eq 0) {
    throw 'Every resolved package was classified as build-only, which cannot be right.'
}

# --- Rendering ----------------------------------------------------------------------------------------

function Format-Cell {
    param([string]$Value)

    # A pipe inside a cell would end the column early. Nothing in the graph does this today; it costs one
    # line to make sure a future package cannot silently corrupt the table.
    return ($Value -replace '\|', '\|')
}

function New-ManifestText {
    $lines = foreach ($package in $packages) {
        "$($package.Id)|$($package.Version)|$($package.License)"
    }

    $header = @(
        '# Generated by scripts/generate-notices.ps1. Do not edit by hand.',
        '#',
        '# Every package NuGet resolved for the desktop application, with the SPDX identifier of its',
        '# licence. build/licenses/PackageLicenses.targets checks this file on every build: an entry whose',
        '# licence is not in allowed-licenses.txt fails the build, as does a package that reached the build',
        '# without reaching this file.',
        '#',
        '# Format: <PackageId>|<Version>|<SPDX>'
    )

    return (($header + $lines) -join "`n") + "`n"
}

function New-NoticesText {
    $usedLicenses = @($shipping | Select-Object -ExpandProperty License -Unique | Sort-Object)

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('# Third-party notices')
    $lines.Add('')
    $lines.Add('RemoteFlow is MIT licensed (see [LICENSE](LICENSE)) and ships the third-party packages listed')
    $lines.Add('below inside its binaries. Their licences are reproduced in full after the table.')
    $lines.Add('')
    $lines.Add('This file is generated from the packages NuGet actually resolved for the desktop application,')
    $lines.Add('transitive dependencies included. Regenerate it rather than editing it:')
    $lines.Add('')
    $lines.Add('```shell')
    $lines.Add('pwsh ./scripts/generate-notices.ps1')
    $lines.Add('```')
    $lines.Add('')
    $lines.Add("## Packages ($($shipping.Count))")
    $lines.Add('')
    $lines.Add('| Package | Version | Licence | Copyright |')
    $lines.Add('| --- | --- | --- | --- |')
    foreach ($package in $shipping) {
        $name = if ($package.ProjectUrl) {
            "[$(Format-Cell $package.Id)]($($package.ProjectUrl))"
        }
        else {
            Format-Cell $package.Id
        }

        $copyright = if ($package.Copyright) { Format-Cell $package.Copyright } else { '—' }
        $lines.Add("| $name | $($package.Version) | $($package.License) | $copyright |")
    }

    $lines.Add('')
    $lines.Add('## Licence texts')
    $lines.Add('')
    foreach ($license in $usedLicenses) {
        $textPath = Join-Path $licenseDirectory "texts/$license.txt"
        if (-not (Test-Path -LiteralPath $textPath)) {
            throw "No licence text at $textPath. Every licence named in the notices is reproduced in full."
        }

        $count = @($shipping | Where-Object { $_.License -eq $license }).Count
        $lines.Add("### $license")
        $lines.Add('')
        $lines.Add("Applies to $count of the packages above.")
        $lines.Add('')
        $lines.Add('```text')
        foreach ($textLine in (Get-Content -LiteralPath $textPath)) {
            $lines.Add($textLine)
        }
        $lines.Add('```')
        $lines.Add('')
    }

    return (($lines -join "`n").TrimEnd() + "`n")
}

$manifestText = New-ManifestText
$noticesText = New-NoticesText

# --- Writing, or checking -----------------------------------------------------------------------------

function Read-FileText {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    # Normalised: a checkout on Windows may have CRLF on disk regardless of what was committed, and a
    # line-ending difference is not a licence difference.
    return ([System.IO.File]::ReadAllText($Path) -replace "`r`n", "`n")
}

# UTF-8 without a BOM and LF endings, matching .gitattributes.
function Write-FileText {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text
    )

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        [System.IO.Directory]::CreateDirectory($directory) | Out-Null
    }

    [System.IO.File]::WriteAllText($Path, $Text, [System.Text.UTF8Encoding]::new($false))
}

if ($Verify) {
    $stale = @()
    foreach ($candidate in @(
            @{ Path = $noticesPath; Expected = $noticesText; Name = 'THIRD-PARTY-NOTICES.md' },
            @{ Path = $manifestPath; Expected = $manifestText; Name = 'build/licenses/package-licenses.txt' })) {
        $actual = Read-FileText -Path $candidate.Path
        if ($null -eq $actual) {
            $stale += "$($candidate.Name) does not exist"
        }
        elseif ($actual -ne $candidate.Expected) {
            $stale += "$($candidate.Name) is out of date"
        }
    }

    if ($stale.Count -gt 0) {
        throw @"
$($stale -join '; ').

The package graph and the checked-in notices disagree. Regenerate and commit the result:

    pwsh ./scripts/generate-notices.ps1
"@
    }

    Write-Host "Notices are current: $($shipping.Count) shipping packages, $($packages.Count) resolved." -ForegroundColor Green
    return
}

Write-FileText -Path $noticesPath -Text $noticesText
Write-FileText -Path $manifestPath -Text $manifestText

Write-Host ''
Write-Host "Wrote THIRD-PARTY-NOTICES.md ($($shipping.Count) shipping packages)." -ForegroundColor Green
Write-Host "Wrote build/licenses/package-licenses.txt ($($packages.Count) resolved packages)."
$packages | Group-Object License | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Name): $($_.Count)"
}
