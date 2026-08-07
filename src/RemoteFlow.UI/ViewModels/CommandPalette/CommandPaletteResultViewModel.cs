using RemoteFlow.Application.Queries;

namespace RemoteFlow.UI.ViewModels.CommandPalette;

public sealed class CommandPaletteResultViewModel(ConnectionListItem connection)
{
    public ConnectionListItem Connection { get; } = connection;

    public Guid Id => Connection.Id;

    public string Name => Connection.Name;

    public string Location => string.IsNullOrWhiteSpace(Connection.FolderPath) ? "/" : Connection.FolderPath;

    public string Host => $"{Connection.Host}:{Connection.Port}";

    public string Description => $"{Location} • {Host}";
}
