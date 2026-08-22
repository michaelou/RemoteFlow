# Object storage

RemoteFlow browses and transfers S3 and Azure Blob Storage from the **Storage** page: your own filesystem
on the left, the bucket or container on the right, and the transfer queue along the bottom.

## Setting up an account

Create a connection on the **Connections** page and choose **S3** or **Azure Blob** as the protocol.

| | S3 | Azure Blob |
| --- | --- | --- |
| Identifier | Access key ID | Storage account name |
| Secret | Secret access key | Account key |
| Region | Required (`eu-west-2`, …) | Carried in the account |
| Host and port | `s3.{region}.amazonaws.com`, 443 | `{account}.blob.core.windows.net`, 443 |

The host and port fill themselves in and stop doing so the moment you type over them, which is how a
sovereign-cloud account such as `*.blob.core.chinacloudapi.cn` is reached.

The secret goes in the platform keychain like every other RemoteFlow credential — Windows Credential
Manager, macOS Keychain, or libsecret, falling back to the encrypted file vault. It never goes in the
database, and a plaintext backup never contains it. Clients are always built with the key you entered:
RemoteFlow never falls through to the AWS SDK's credential chain, so it cannot silently use
`~/.aws/credentials` or an instance metadata endpoint you did not mean to reach.

### S3-compatible services

A custom endpoint and a path-style-addressing switch make the S3 protocol work against MinIO, Ceph/RGW,
Backblaze B2, Cloudflare R2 and Wasabi. Most of them need path style; AWS itself does not.

### Scoping to one bucket

Leave **Bucket or container** blank to browse the whole account. Set it when the key is scoped to a single
bucket and therefore cannot list buckets — which is normal in production. An optional **Root prefix**
starts browsing further in, and the pane will not walk above it.

If a key cannot list buckets, RemoteFlow says so and names the remedy rather than showing an empty
account.

## Browsing

The right-hand pane lists one level at a time. A common prefix appears as a folder; the zero-byte marker
object that both vendors' consoles create for an empty folder is shown once, as that folder, and never
also as an empty file.

**"Starts with" is not a search.** Both providers can narrow a listing by prefix and neither can search by
substring, so the filter box re-requests the listing with a longer prefix. Typing `2024` under
`media-prod/` asks the provider for `2024*` in one request; it does not sift through rows already on
screen, and it will not find `raw-2024`.

**A listing stops at 10,000 rows.** Ten pages of a thousand. At the cap the **Load more** button is
replaced by a line reading *"10,000 of many shown. Narrow the prefix, or use the path box to go deeper."*
RemoteFlow never claims a total: S3 cannot cheaply count a prefix, so a number would be a guess. Sorting a
truncated listing sorts the rows that are loaded and nothing else, and the sort-header tooltip says so.

**There is no rename.** S3's nearest primitive is a server-side copy — billed per byte, capped at 5 GiB —
followed by a delete. RemoteFlow does not disguise that as a rename, so `F2` works on the local pane only.

## Transferring

Select rows and press **Upload** or **Download**, drag between the panes, or press `Enter`. A folder is
counted first and confirmed before a byte moves, because "transfer 1 item" and "transfer 41,000 items" are
the same drag.

Objects above 16 MiB go in parallel parts — 8 MiB parts for a 4 GiB object, 64 MiB for a 500 GB one, four
in flight. Memory stays flat whatever the object size. A failed part is retried on its own rather than
restarting the transfer, and the speed and time-remaining figures follow a five-second window rather than
the average since the transfer started, so a link that slows down is reported honestly.

The queue at the foot of the page is the application-wide queue, the same one the **Transfers** page shows.
Clearing completed items from either surface clears both.

### When the destination already exists

By default RemoteFlow overwrites: a put is atomic and idempotent in both providers, and there is no
partial-object state to protect. Change **Storage conflict default** to *Prompt* if you would rather be
asked — worth doing on an unversioned bucket, where an overwrite cannot be undone. When prompted on a
multi-item transfer, **Apply to all remaining items** answers the rest of that one drag and nothing else.

## Set a lifecycle rule for incomplete uploads

**Do this once per bucket.** When a chunked upload is cancelled or fails, RemoteFlow aborts it, and it
tells you plainly if the abort itself failed. But a process kill or a power cut skips the abort entirely,
and the parts left behind are stored and billed until something removes them. No client can promise
otherwise.

On **S3**, add a lifecycle rule with `AbortIncompleteMultipartUpload` at seven days:

```json
{
  "Rules": [
    {
      "ID": "abort-incomplete-multipart-uploads",
      "Status": "Enabled",
      "Filter": {},
      "AbortIncompleteMultipartUpload": { "DaysAfterInitiation": 7 }
    }
  ]
}
```

```shell
aws s3api put-bucket-lifecycle-configuration \
  --bucket my-bucket \
  --lifecycle-configuration file://lifecycle.json
```

On **Azure Blob** there is nothing to do: uncommitted blocks are invisible, unbilled, and garbage-collected
after seven days.

## What RemoteFlow does not do

- **Create or delete buckets and containers.** Bucket creation carries region, ownership,
  public-access-block, versioning and lifecycle decisions a two-field dialog cannot make safely, and a
  mis-created public bucket is a security incident rather than a UX regression. Use the provider console.
- **Rename or server-side copy**, object versioning, SSE-C and SSE-KMS keys, or lifecycle rules.
- **Remote editing of objects.** Conflict detection on an object is the ETag, not size and mtime, and
  "open a 400 MB object in your editor" is a different feature with different failure modes.
- **Resume a transfer across a restart.** Without a server capability and an identity check a resumed
  offset can splice bytes from two different object versions.

## Keyboard

See the **Storage page** section of [keybindings.md](keybindings.md).

## Where the decisions are written down

- [adr/0019-object-storage-provider-abstraction.md](adr/0019-object-storage-provider-abstraction.md) — the
  provider adapters, the path model, and the credential story.
- [adr/0020-chunked-object-storage-transfers.md](adr/0020-chunked-object-storage-transfers.md) — chunking,
  retry, progress, and the abort guarantee.
- [adr/0021-dual-pane-storage-workspace.md](adr/0021-dual-pane-storage-workspace.md) — the page itself.
