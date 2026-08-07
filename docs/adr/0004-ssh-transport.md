# ADR-0004: SSH transport abstraction

- Status: Accepted
- Date: 2026-08-07

## Context

RemoteFlow requires modern SSH features and a way to handle incompatibilities found in real servers. Direct use of a single library throughout the application would make compatibility changes invasive.

## Decision

Define an `ISshTransport` abstraction in the application boundary. Use Tmds.Ssh as the primary implementation because it is the preferred modern transport. Provide SSH.NET as a fallback implementation for server or authentication scenarios that the primary transport cannot support adequately.

The abstraction covers connection establishment, host-key handling integration, command channels, shell channels, streams, resize, cancellation, and normalized errors. Selection of the fallback is explicit and observable rather than silently changing security behavior.

## Consequences

Transport-specific types remain outside use cases and compatibility can improve incrementally. The adapter surface must be kept small enough to preserve library capabilities. Both implementations require security updates and focused integration tests against representative servers.
