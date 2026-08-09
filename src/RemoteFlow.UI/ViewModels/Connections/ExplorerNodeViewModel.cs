using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Queries;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.UI.ViewModels.Connections;

public enum ExplorerNodeKind
{
    Favorites = 1,
    Recent = 2,
    Folder = 3,
    Connection = 4,
}

public enum ExplorerAction
{
    Connect = 1,
    OpenSftp = 2,
    OpenRdp = 3,
    Edit = 4,
    Duplicate = 5,
    Delete = 6,
    NewFolder = 7,
}

public sealed record EnvironmentBadgeViewModel(string Icon, string Text, IBrush Background);

public sealed partial class ExplorerNodeViewModel : ObservableObject
{
    private readonly Func<ExplorerNodeViewModel, ExplorerAction, Task> _executeAction;
    private readonly Func<ExplorerNodeViewModel, string, Task<bool>> _rename;

    public ExplorerNodeViewModel(
        ExplorerNodeKind kind,
        string name,
        Func<ExplorerNodeViewModel, ExplorerAction, Task> executeAction,
        Func<ExplorerNodeViewModel, string, Task<bool>> rename,
        Guid? id = null,
        Folder? folder = null,
        ConnectionListItem? connection = null,
        string icon = "")
    {
        Kind = kind;
        Name = name;
        EditName = name;
        _executeAction = executeAction;
        _rename = rename;
        Id = id;
        Folder = folder;
        Connection = connection;
        Icon = icon;
        IsExpanded = folder?.IsExpanded ?? kind is ExplorerNodeKind.Favorites or ExplorerNodeKind.Recent;
        Badge = connection is null ? null : CreateBadge(connection.Environment, connection.ColorOverrideHex);
        SecondaryText = connection is null ? null : $"{connection.Host}:{connection.Port}";
        ConnectCommand = new AsyncRelayCommand(
            () => ExecuteAsync(ExplorerAction.Connect),
            () => Kind == ExplorerNodeKind.Connection);
        OpenSftpCommand = new AsyncRelayCommand(
            () => ExecuteAsync(ExplorerAction.OpenSftp),
            () => Connection?.Protocol is ProtocolType.Ssh or ProtocolType.Sftp);
        OpenRdpCommand = new AsyncRelayCommand(
            () => ExecuteAsync(ExplorerAction.OpenRdp),
            () => Connection?.Protocol == ProtocolType.Rdp);
        EditCommand = new AsyncRelayCommand(
            () => ExecuteAsync(ExplorerAction.Edit),
            () => Kind == ExplorerNodeKind.Connection);
        DuplicateCommand = new AsyncRelayCommand(
            () => ExecuteAsync(ExplorerAction.Duplicate),
            () => Kind == ExplorerNodeKind.Connection);
        DeleteCommand = new AsyncRelayCommand(
            () => ExecuteAsync(ExplorerAction.Delete),
            () => Kind is ExplorerNodeKind.Connection or ExplorerNodeKind.Folder);
        NewFolderCommand = new AsyncRelayCommand(
            () => ExecuteAsync(ExplorerAction.NewFolder),
            () => Kind == ExplorerNodeKind.Folder);
        BeginRenameCommand = new RelayCommand(BeginRename, () => Kind is ExplorerNodeKind.Connection or ExplorerNodeKind.Folder);
        CommitRenameCommand = new AsyncRelayCommand(CommitRenameAsync);
        CancelRenameCommand = new RelayCommand(CancelRename);
    }

    public event Action<ExplorerNodeViewModel, bool>? ExpansionChanged;

    public ExplorerNodeKind Kind { get; }

    public Guid? Id { get; }

    public Folder? Folder { get; }

    public ConnectionListItem? Connection { get; }

    public string Icon { get; }

    public string? SecondaryText { get; }

    public EnvironmentBadgeViewModel? Badge { get; }

    public ObservableCollection<ExplorerNodeViewModel> Children { get; } = [];

    public IAsyncRelayCommand ConnectCommand { get; }

    public IAsyncRelayCommand OpenSftpCommand { get; }

    public IAsyncRelayCommand OpenRdpCommand { get; }

    public IAsyncRelayCommand EditCommand { get; }

    public IAsyncRelayCommand DuplicateCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    public IAsyncRelayCommand NewFolderCommand { get; }

    public IRelayCommand BeginRenameCommand { get; }

    public IAsyncRelayCommand CommitRenameCommand { get; }

    public IRelayCommand CancelRenameCommand { get; }

    public bool IsVirtual => Kind is ExplorerNodeKind.Favorites or ExplorerNodeKind.Recent;

    [ObservableProperty]
    public partial string Name { get; private set; }

    [ObservableProperty]
    public partial string EditName { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsRenaming { get; private set; }

    internal static EnvironmentBadgeViewModel? CreateBadge(
        EnvironmentKind environment,
        string? colorOverrideHex)
    {
        if (environment == EnvironmentKind.Unspecified && string.IsNullOrWhiteSpace(colorOverrideHex))
        {
            return null;
        }

        var (icon, text, fallback) = environment switch
        {
            EnvironmentKind.Unspecified => ("●", "CUSTOM", "#6CB6FF"),
            EnvironmentKind.Development => ("●", "DEV", "#5DE28C"),
            EnvironmentKind.Staging => ("◆", "STAGE", "#FFCA58"),
            EnvironmentKind.Production => ("⚠", "PROD", "#FF7B72"),
            _ => throw new ArgumentOutOfRangeException(nameof(environment)),
        };
        var color = Color.TryParse(colorOverrideHex, out var customColor)
            ? customColor
            : Color.Parse(fallback);
        return new EnvironmentBadgeViewModel(icon, text, new SolidColorBrush(color));
    }

    private Task ExecuteAsync(ExplorerAction action)
    {
        return _executeAction(this, action);
    }

    private void BeginRename()
    {
        EditName = Name;
        IsRenaming = true;
    }

    private async Task CommitRenameAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            return;
        }

        if (await _rename(this, EditName.Trim()).ConfigureAwait(true))
        {
            Name = EditName.Trim();
            IsRenaming = false;
        }
    }

    private void CancelRename()
    {
        EditName = Name;
        IsRenaming = false;
    }

    partial void OnIsExpandedChanged(bool value)
    {
        ExpansionChanged?.Invoke(this, value);
    }
}
