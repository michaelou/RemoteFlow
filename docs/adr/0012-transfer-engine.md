# ADR 0012: Atomic bounded SFTP transfers

## Decision

RemoteFlow transfers at most three files concurrently by default. Uploads and downloads write to a
`.part` path and publish the final name only after the complete stream has been flushed. Existing targets
require an explicit conflict decision, and transient failures receive one retry.

Publishing an upload over a name that already exists cannot be a plain rename: SFTP v3 has no
overwriting rename, and OpenSSH implements `SSH_FXP_RENAME` with `link()`+`unlink()`, so it refuses an
existing destination. Every upload therefore publishes through `SftpPublisher`, which renames the
current file aside, renames the upload into place, and then drops the aside copy — restoring it if the
publishing rename fails. The replaced file's permissions are re-applied to the published one, so
replacing a file never silently changes its mode.

Recursive transfers preserve directory structure and report progress per file. A queued operation owns
its cancellation independently, so cancelling one waiter does not affect transfers already running or
other queued work.

## Deferred

Transfer resume is deliberately deferred to v2. V1 always restarts a failed transfer from byte zero;
persisting resumable offsets without a server capability and identity check would risk combining bytes
from different file versions.
