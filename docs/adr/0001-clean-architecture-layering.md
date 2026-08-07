# ADR-0001: Clean Architecture layering

- Status: Accepted
- Date: 2026-08-07

## Context

RemoteFlow needs a structure that keeps business rules independent from Avalonia, database choices, SSH libraries, and operating-system services. Those integrations will change at different rates and need testing without a desktop process.

## Decision

Use Clean Architecture layers. The Domain project contains entities, value objects, and domain rules with no outward framework dependency. Application contains use cases and interfaces and depends only on Domain. Infrastructure implements Application interfaces and contains EF Core, transport, credential, and filesystem adapters. The desktop/UI project depends on Application and Infrastructure only at its composition root.

Project references point inward: UI -> Application -> Domain and Infrastructure -> Application/Domain. Domain does not reference any other RemoteFlow project; Application does not reference UI or Infrastructure. Dependency injection composes concrete adapters at the edge.

## Consequences

Use cases can be tested with fakes and the UI can be replaced without moving business logic. New integrations have a clear home in Infrastructure. The boundary adds interfaces and mapping code, so trivial features must not bypass it merely for convenience.
