using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Connections;

public sealed class ConnectionDetailsViewModel(
    Connection connection,
    string folderPath,
    IReadOnlyList<string> tags,
    DateTimeOffset? lastConnectedUtc,
    Func<ConnectionOpenMode, Task> open,
    Func<Task> edit,
    Func<Task> duplicate,
    Func<Task> delete)
{
    public Connection Connection { get; } = connection;

    public string Name => Connection.Name;

    public string HostAndPort => $"{Connection.Host}:{Connection.Port}";

    public string Protocol => Connection.Protocol.ToString().ToUpperInvariant();

    public string Authentication => Connection.AuthMethod.ToString();

    public string Environment => Connection.Environment.ToString();

    public string FolderPath { get; } = folderPath;

    public string TagsText { get; } = tags.Count == 0 ? "None" : string.Join(", ", tags);

    public string Notes => string.IsNullOrWhiteSpace(Connection.Notes) ? "None" : Connection.Notes;

    public string LastConnectedText { get; } = lastConnectedUtc?.ToLocalTime()
        .ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Never";

    public IAsyncRelayCommand ConnectCommand { get; } = new AsyncRelayCommand(() => open(ConnectionOpenMode.Default));

    public IAsyncRelayCommand OpenSftpCommand { get; } = new AsyncRelayCommand(
        () => open(ConnectionOpenMode.Sftp),
        () => connection.SupportsSftp);

    public IAsyncRelayCommand LaunchRdpCommand { get; } = new AsyncRelayCommand(
        () => open(ConnectionOpenMode.Rdp),
        () => connection.Protocol == ProtocolType.Rdp);

    public IAsyncRelayCommand EditCommand { get; } = new AsyncRelayCommand(edit);

    public IAsyncRelayCommand DuplicateCommand { get; } = new AsyncRelayCommand(duplicate);

    public IAsyncRelayCommand DeleteCommand { get; } = new AsyncRelayCommand(delete);
}
