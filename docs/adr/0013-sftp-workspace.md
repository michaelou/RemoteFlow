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

## Amendment: the dual-pane mode arrived, and this ADR still governs SFTP

The prediction above held. [ADR-0021](0021-dual-pane-storage-workspace.md) added a dual-pane workspace for
object storage without changing the SFTP or transfer contracts: `TransferEngine`, `TransferContracts.cs`,
`TransfersPageViewModel.cs` and `SftpWorkspaceViewModel.cs` are all untouched by it.

This ADR is amended rather than superseded. The **SFTP** workspace stays single-pane and remote-first, with
the staging-directory drag-out described above, and is still governed by everything here. Nothing in
ADR-0021 applies to it.
