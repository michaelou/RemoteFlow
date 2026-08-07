[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'tests/RemoteFlow.Ssh.IntegrationTests'

dotnet test $project --filter 'Category=Integration'
if ($LASTEXITCODE -ne 0) {
    throw "SSH integration tests failed with exit code $LASTEXITCODE."
}
