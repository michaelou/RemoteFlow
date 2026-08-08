# ADR 0012: Atomic bounded SFTP transfers

## Decision

RemoteFlow transfers at most three files concurrently by default. Uploads and downloads write to a
`.part` path and publish the final name only after the complete stream has been flushed. Existing targets
require an explicit conflict decision, and transient failures receive one retry.

Recursive transfers preserve directory structure and report progress per file. A queued operation owns
its cancellation independently, so cancelling one waiter does not affect transfers already running or
other queued work.

## Deferred

Transfer resume is deliberately deferred to v2. V1 always restarts a failed transfer from byte zero;
persisting resumable offsets without a server capability and identity check would risk combining bytes
from different file versions.
