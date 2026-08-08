# ADR 0014: SFTP permission editing

## Status

Accepted

## Context

Unix permission changes are portable across the supported SFTP transports, while ownership changes often require elevation and vary substantially between servers.

## Decision

RemoteFlow v1 edits the standard Unix mode bits, including setuid, setgid, and sticky. Recursive updates use separate directory and file modes; the file mode omits execute bits by default so making directories traversable does not silently make every file executable. The complete reachable tree is enumerated before changes begin, and failures are collected per path without rolling back successful changes.

Owner and group are displayed as metadata only. `chown`, `chgrp`, and ACL editing are out of scope for v1.

## Consequences

Users receive predictable chmod behavior and complete partial-failure reporting. Ownership remains visible without implying that RemoteFlow can safely elevate or provide a consistent cross-server ownership workflow.
