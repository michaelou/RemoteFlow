# RemoteFlow

RemoteFlow is a desktop application for organizing remote connections, credentials, and terminal sessions in one local-first workspace. It is currently **pre-alpha**; its architecture and public behavior may change before the first release.

## Technology stack

- .NET and Avalonia Desktop
- EF Core with SQLite for local data
- Tmds.Ssh, with SSH.NET as a compatibility fallback
- SvcSystems.UI.Terminal, XTerm.NET, and Porta.Pty for the provisional terminal stack

## Build and test

```shell
dotnet build
dotnet test
dotnet test --filter Category=Integration
```

See [the v1 requirements](docs/requirements-v1.md) when that document is available, and the ADR log in [docs/adr](docs/adr).
