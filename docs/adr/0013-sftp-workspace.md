# ADR 0013: Remote-first SFTP workspace

## Status

Accepted

## Context

The first SFTP workspace must browse very large remote directories and interoperate with operating-system drag and drop without presenting two competing navigation models.

## Decision

RemoteFlow presents one virtualized, remote-first file pane. Local paths enter through the operating-system picker or drag and drop. Files dragged out are downloaded atomically into a unique temporary staging directory before the native drag begins, because native consumers require each advertised path to exist for the entire drop operation.

Folder ordering is always stable and directory-first. Navigation failures remain inline and retain the last usable listing.

## Consequences

The workspace stays focused and works naturally with the user's existing file manager. A dual-pane local/remote mode is deferred to a future version and can be added without changing the SFTP or transfer contracts.
