# ADR-0006: Host key verification

- Status: Accepted
- Date: 2026-08-07

## Context

SSH server identity must be verified without unexpectedly modifying a user's global SSH configuration. RemoteFlow also needs a clear first-connection experience for users who do not manage `known_hosts` files themselves.

## Decision

Use trust on first use (TOFU) with a RemoteFlow-owned trust store. On first connection, display the host key fingerprint and require the user to approve it. On later connections, require an exact match and surface changes as a security warning that requires explicit resolution.

Never read from or write to `~/.ssh/known_hosts` as the RemoteFlow trust store. Import/export, if introduced, must be explicit and must preserve the distinction between external data and RemoteFlow's trusted entries.

## Consequences

The application avoids mutating user configuration and provides a consistent UI. TOFU cannot protect the very first connection from a network attacker, so fingerprint verification is important for sensitive hosts. Trust-store encryption and backup behavior must be coordinated with credential and backup decisions.
