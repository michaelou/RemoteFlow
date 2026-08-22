# RemoteFlow backup format v1

RemoteFlow backups are standard ZIP archives containing a versioned logical snapshot. Version 1 is a
public compatibility promise: every future RemoteFlow release must retain a reader for this schema.

## Entries

| Entry | Contents |
| --- | --- |
| `manifest.json` | Format and application versions, creation time, optional machine name, entity counts, and credential metadata. |
| `connections.json` | Connections and their non-secret credential references and protocol options. |
| `folders.json` | The complete folder records needed to reproduce stable paths. |
| `tags.json` | Tags. |
| `connection-tags.json` | Connection-to-tag relationships. |
| `settings.json` | Included application settings. |
| `host-keys.json` | Trusted host keys and their trust metadata. |
| `credentials.enc` | Optional encrypted credential records. This entry is never written when credentials are excluded. |

Every JSON entity record carries its stable identifier. Export and import must preserve these identifiers,
which makes re-import idempotent and keeps relationships intact. Recent-connection history is transient UI
state and is intentionally excluded.

`manifest.json` has the following shape:

```json
{
  "formatVersion": 1,
  "appVersion": "1.0.0",
  "createdUtc": "2026-08-08T12:00:00+00:00",
  "machineName": "workstation",
  "counts": {
    "connections": 1,
    "folders": 1,
    "tags": 1,
    "connectionTags": 1,
    "settings": 1,
    "hostKeys": 1
  },
  "includesCredentials": false,
  "credentialKdf": null
}
```

`machineName` is optional and omitted when the user opts out. When `includesCredentials` is true,
`credentialKdf` records `algorithm`, `m` (memory in KiB), `t` (iterations), `p` (parallelism), and the
base64-encoded salt. The encrypted envelope itself is specified with the credential feature.

## Compatibility rules

- Readers must ignore unknown JSON fields. This lets a v1 reader accept compatible additive changes.
- **Unknown enum *members* are not covered by that rule.** Enums are written as camelCase strings, so an
  archive naming a protocol, credential kind or policy the reading build does not know fails the whole
  import rather than skipping the one record. A backup written by a newer RemoteFlow containing an S3 or
  Azure Blob connection (`"protocol": "s3"`, `"protocol": "azureBlob"`) is therefore refused by a build
  that predates them, with `The backup contains invalid JSON near '$[0].protocol'.` This is deliberate:
  the alternative — an `Unsupported` fallback member — would silently import a connection that build cannot
  open. Export from the older build, or upgrade before importing.
- Readers must refuse an unknown `formatVersion` before any data is written. Major schema changes use a
  new integer version and a dedicated reader.
- A missing entity entry is interpreted as an empty collection when that is semantically valid. The
  manifest is always required, and its counts must agree with the entries that are present.
- Duplicate known ZIP entries, malformed JSON, invalid ZIP data, and manifest/content count mismatches are
  rejected with a specific error.
- Writers indent JSON entries with two spaces and end lines with `\n` on every operating system, so the
  same snapshot produces the same bytes whether it was written on Windows, Linux, or macOS. The manifest
  hash that authenticates the credential envelope is taken over those bytes; readers must not assume any
  particular line ending, because a hand-edited archive may carry another.

## Security boundary

All entries other than `credentials.enc` are plaintext. They may contain connection metadata, usernames,
paths, public host keys, and credential-store references, but never password or passphrase values. Secret
material is permitted only inside the authenticated encrypted credential envelope. A backup containing
credentials must not be treated as safe merely because the ZIP container itself can be opened.
