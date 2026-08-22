using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Connections;

public sealed partial class ConnectionDetailsViewModel(
    Connection connection,
    string folderPath,
    IReadOnlyList<string> tags,
    DateTimeOffset? lastConnectedUtc,
    Func<ConnectionOpenMode, Task> open,
    Func<Task> edit,
    Func<Task> duplicate,
    Func<Task> delete,
    Func<Task<SystemTerminalLaunchResult>>? openSystemTerminal = null,
    bool showExplicitExternalRdpAction = false) : ObservableObject
{
    public Connection Connection { get; } = connection;

    public string Name => Connection.Name;

    public string HostAndPort => $"{Connection.Host}:{Connection.Port}";

    public string Protocol => Connection.Protocol.GetDisplayName();

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

    public bool ShowExplicitExternalRdpAction { get; } =
        showExplicitExternalRdpAction && connection.Protocol == ProtocolType.Rdp;

    public IAsyncRelayCommand OpenExternalRdpCommand { get; } = new AsyncRelayCommand(
        () => open(ConnectionOpenMode.RdpExternal),
        () => connection.Protocol == ProtocolType.Rdp && showExplicitExternalRdpAction);

    public IAsyncRelayCommand EditCommand { get; } = new AsyncRelayCommand(edit);

    public IAsyncRelayCommand DuplicateCommand { get; } = new AsyncRelayCommand(duplicate);

    public IAsyncRelayCommand DeleteCommand { get; } = new AsyncRelayCommand(delete);

    [ObservableProperty]
    public partial string? ExternalLaunchMessage { get; private set; }

    [RelayCommand(CanExecute = nameof(CanOpenSystemTerminal))]
    private async Task OpenSystemTerminalAsync()
    {
        if (openSystemTerminal is null)
        {
            return;
        }

        var result = await openSystemTerminal().ConfigureAwait(true);
        ExternalLaunchMessage = result.ErrorMessage;
    }

    private bool CanOpenSystemTerminal()
    {
        return openSystemTerminal is not null && Connection.Protocol is ProtocolType.Ssh or ProtocolType.Sftp;
    }
}
