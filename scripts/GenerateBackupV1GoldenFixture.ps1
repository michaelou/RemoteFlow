$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$fixtureDirectory = Join-Path $repositoryRoot 'tests/RemoteFlow.Application.Tests/Fixtures'
$fixturePath = Join-Path $fixtureDirectory 'backup-v1-golden.zip'
$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "RemoteFlow-Golden-$([guid]::NewGuid().ToString('N'))"
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)

try {
  [System.IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
  [System.IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
  $created = '2026-08-08T12:00:00+00:00'
  $modified = '2026-08-08T12:01:00+00:00'
  $rocket = [char]::ConvertFromUtf32(0x1F680)
  $siren = [char]::ConvertFromUtf32(0x1F6A8)

  $entries = [ordered]@{
    'manifest.json' = [ordered]@{
      formatVersion = 1
      appVersion = '1.0.0-test'
      createdUtc = $created
      machineName = 'fixture-machine'
      counts = [ordered]@{ connections = 1; folders = 1; tags = 1; connectionTags = 1; settings = 1; hostKeys = 1 }
      includesCredentials = $false
      credentialKdf = $null
    }
    'connections.json' = @([ordered]@{
      id = '11111111-1111-1111-1111-111111111111'
      name = 'Production shell'
      host = 'example.test'
      port = 22
      protocol = 'ssh'
      username = 'operator'
      authMethod = 'password'
      notes = "Unicode $rocket"
      folderId = '22222222-2222-2222-2222-222222222222'
      isFavorite = $true
      environment = 'production'
      colorOverrideHex = '#112233'
      sortOrder = 7
      concurrencyStamp = '44444444-4444-4444-4444-444444444444'
      createdUtc = $created
      modifiedUtc = $modified
      credential = [ordered]@{ kind = 'password'; storeKey = 'credential-key'; storeProvider = 'test-store'; updatedUtc = $created }
      ssh = [ordered]@{ keepAliveSeconds = 30; terminalType = 'xterm-256color'; privateKeyPath = 'C:/keys/id_ed25519'; initialCommand = 'tmux'; startupDirectory = '/srv'; hostKeyPolicy = 'strict'; requestPty = $true }
      sftp = [ordered]@{ remoteRootPath = '/srv'; localDownloadPath = 'C:/Downloads'; preserveTimestamps = $true; showHiddenFiles = $true }
      rdp = [ordered]@{ domain = 'EXAMPLE'; fullScreen = $false; width = 1920; height = 1080; multimon = $false; redirectClipboard = $true; redirectDrives = $false }
    })
    'folders.json' = @([ordered]@{
      id = '22222222-2222-2222-2222-222222222222'; name = 'Production'; parentId = $null; path = '/Production'; depth = 0
      sortOrder = 2; isExpanded = $true; concurrencyStamp = '55555555-5555-5555-5555-555555555555'; createdUtc = $created; modifiedUtc = $created
    })
    'tags.json' = @([ordered]@{ id = '33333333-3333-3333-3333-333333333333'; name = "Critical $siren"; colorHex = '#FF0000'; createdUtc = $created })
    'connection-tags.json' = @([ordered]@{ connectionId = '11111111-1111-1111-1111-111111111111'; tagId = '33333333-3333-3333-3333-333333333333' })
    'settings.json' = @([ordered]@{ key = 'terminal.fontSize'; value = '14'; modifiedUtc = $created })
    'host-keys.json' = @([ordered]@{
      id = '66666666-6666-6666-6666-666666666666'; host = 'example.test'; port = 22; keyAlgorithm = 'ssh-ed25519'
      publicKeyBase64 = 'AAAAC3NzaC1lZDI1NTE5AAAAIGoldenFixturePublicKey'; sha256Fingerprint = 'SHA256:golden-fixture-fingerprint'
      trustState = 'trusted'; source = 'pinned'; comment = 'Pinned by test'; firstSeenUtc = $created; lastSeenUtc = '2026-08-08T12:02:00+00:00'
    })
  }

  foreach ($entry in $entries.GetEnumerator()) {
    $json = ConvertTo-Json -InputObject $entry.Value -Depth 10
    [System.IO.File]::WriteAllText((Join-Path $temporaryDirectory $entry.Key), $json, $utf8WithoutBom)
  }

  if ([System.IO.File]::Exists($fixturePath)) {
    [System.IO.File]::Delete($fixturePath)
  }

  [System.IO.Compression.ZipFile]::CreateFromDirectory(
    $temporaryDirectory,
    $fixturePath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)
}
finally {
  if ([System.IO.Directory]::Exists($temporaryDirectory)) {
    [System.IO.Directory]::Delete($temporaryDirectory, $true)
  }
}
