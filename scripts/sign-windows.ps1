<#
.SYNOPSIS
Signs Windows artefacts, or does nothing when no certificate is configured.

.DESCRIPTION
RemoteFlow has no code-signing certificate yet, so every release path has to work unsigned. This script
is the single place that knows the difference: with no thumbprint configured it reports that it skipped
and returns success, so the publish script stays one code path.

Configured but unusable is a different situation and fails loudly. A release that silently ships
unsigned because signtool.exe was missing is the outcome worth preventing.

.PARAMETER Path
Files to sign.

.PARAMETER CertificateThumbprint
SHA1 thumbprint of a certificate in the current user's or machine's store. Defaults to
REMOTEFLOW_SIGN_THUMBPRINT. No thumbprint means no signing.

.PARAMETER TimestampUrl
RFC 3161 timestamp server. Defaults to REMOTEFLOW_SIGN_TIMESTAMP_URL, then to DigiCert's. Timestamping
is what keeps a signature valid after the certificate expires.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string[]]$Path,
    [string]$CertificateThumbprint = $env:REMOTEFLOW_SIGN_THUMBPRINT,
    [string]$TimestampUrl = $env:REMOTEFLOW_SIGN_TIMESTAMP_URL
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    Write-Host 'Signing skipped: no certificate configured (set REMOTEFLOW_SIGN_THUMBPRINT to sign).'
    return
}

if ([string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $TimestampUrl = 'http://timestamp.digicert.com'
}

function Resolve-SignTool {
    $onPath = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($onPath) {
        return $onPath.Source
    }

    # Newest Windows SDK first; the architecture directory only decides which signtool runs, not what it
    # can sign.
    $roots = @(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ }
    foreach ($root in $roots) {
        $candidate = Get-ChildItem -Path (Join-Path $root 'Windows Kits\10\bin') -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
            Sort-Object -Property FullName -Descending |
            Select-Object -First 1
        if ($candidate) {
            return $candidate.FullName
        }
    }

    throw 'A signing certificate is configured but signtool.exe was not found. Install the Windows SDK signing tools, or clear REMOTEFLOW_SIGN_THUMBPRINT to publish unsigned.'
}

$signTool = Resolve-SignTool
$missing = $Path | Where-Object { -not (Test-Path -LiteralPath $_) }
if ($missing) {
    throw "Cannot sign files that do not exist: $($missing -join ', ')"
}

Write-Host "Signing $($Path.Count) file(s) with certificate $CertificateThumbprint"
& $signTool sign /fd SHA256 /sha1 $CertificateThumbprint /tr $TimestampUrl /td SHA256 @Path
if ($LASTEXITCODE -ne 0) {
    throw "signtool failed with exit code $LASTEXITCODE."
}
