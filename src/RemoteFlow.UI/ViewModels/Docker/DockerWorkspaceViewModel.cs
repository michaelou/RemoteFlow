using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Queries;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Docker;

/// <summary>One entry in the page's connection picker. Only SSH connections get one.</summary>
public sealed record DockerConnectionChoice(Guid Id, string Name, string Endpoint);

/// <summary>A row in the container list. Rows are updated in place on refresh rather than replaced, so the
/// selection — and the keyboard focus that follows it — survives the five-second refresh.</summary>
public sealed partial class DockerContainerItemViewModel(DockerContainer container) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Id))]
    [NotifyPropertyChangedFor(nameof(Name))]
    [NotifyPropertyChangedFor(nameof(Image))]
    [NotifyPropertyChangedFor(nameof(Status))]
    [NotifyPropertyChangedFor(nameof(Ports))]
    [NotifyPropertyChangedFor(nameof(Project))]
    [NotifyPropertyChangedFor(nameof(State))]
    [NotifyPropertyChangedFor(nameof(StateText))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(CanOpenShell))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial DockerContainer Container { get; private set; } = container;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuText))]
    [NotifyPropertyChangedFor(nameof(MemoryText))]
    public partial DockerContainerStats? Stats { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(CanOpenShell))]
    public partial bool IsBusy { get; set; }

    public string Id => Container.Id;

    public string Name => Container.Name;

    public string Image => Container.Image;

    public string Status => Container.Status;

    public string Ports => Container.Ports;

    /// <summary>The compose project, or a dash for a container started on its own.</summary>
    public string Project => Container.ComposeProject ?? "—";

    public DockerContainerState State => Container.State;

    public string StateText => Container.State == DockerContainerState.Unknown
        ? "unknown"
        : Container.State.ToString().ToLowerInvariant();

    public bool IsRunning => Container.IsRunning;

    public bool CanStart => !IsBusy && !IsRunning;

    public bool CanStop => !IsBusy && IsRunning;

    /// <summary>Docker refuses to remove a running container without <c>--force</c>, and RemoteFlow does not
    /// send <c>--force</c>: stopping first is the deliberate second step.</summary>
    public bool CanRemove => !IsBusy && !IsRunning;

    public bool CanOpenShell => !IsBusy && Container.State == DockerContainerState.Running;

    public string CpuText => Stats?.CpuPercent is { } cpu ? cpu.ToString("0.0", CultureInfo.CurrentCulture) + " %" : "—";

    public string MemoryText => Stats is { MemoryUsage.Length: > 0 } stats ? stats.MemoryUsage : "—";

    /// <summary>What a screen reader says for the row: the name alone would not say whether it is up.</summary>
    public string AccessibleName => $"{Name}, {StateText}, {Image}";

    public void Update(DockerContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        if (container != Container)
        {
            Container = container;
        }

        if (!container.IsRunning)
        {
            Stats = null;
        }
    }

    public void ApplyStats(DockerContainerStats? stats)
    {
        Stats = IsRunning ? stats : null;
    }
}

/// <summary>The Docker page: the containers on one server, what they are doing, and the handful of things
/// worth doing to them without opening a terminal. Everything goes through the <c>docker</c> CLI on an SSH
/// connection the page holds open; see ADR-0026.</summary>
public sealed partial class DockerWorkspaceViewModel(
    IDockerWorkspaceSessionFactory sessions,
    IConfirmationDialogService confirmation,
    IContainerShellOpener? shells = null,
    IConnectionQueryService? connectionQueries = null,
    IClipboardService? clipboard = null) : PageViewModel("Docker"), IAsyncDisposable
{
    private readonly IDockerWorkspaceSessionFactory _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    private readonly IConfirmationDialogService _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
    private readonly List<DockerContainerItemViewModel> _all = [];
    private DockerWorkspaceSession? _session;
    private Guid? _attachedConnectionId;
    private CancellationTokenSource? _autoRefresh;
    private int _refreshing;

    public ObservableCollection<DockerConnectionChoice> AvailableConnections { get; } = [];

    /// <summary>The rows that pass the filter, ordered by compose project and then by name, so the services
    /// of one project sit together.</summary>
    public ObservableCollection<DockerContainerItemViewModel> Containers { get; } = [];

    public DockerLogsViewModel Logs { get; } = new(clipboard);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectSelectedCommand))]
    public partial DockerConnectionChoice? SelectedConnection { get; set; }

    [ObservableProperty]
    public partial DockerContainerItemViewModel? SelectedContainer { get; set; }

    [ObservableProperty]
    public partial string ConnectionTitle { get; private set; } = "Docker";

    [ObservableProperty]
    public partial string? ServerVersion { get; private set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowStopped { get; set; } = true;

    /// <summary>Refresh the list every <see cref="AutoRefreshInterval"/> while the page is on screen.</summary>
    [ObservableProperty]
    public partial bool AutoRefresh { get; set; } = true;

    public TimeSpan AutoRefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string? FeedbackMessage { get; private set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    public bool IsConnected => _session is not null;

    public bool HasConnectionChoices => AvailableConnections.Count > 0;

    public bool HasNoContainers => IsConnected && !IsLoading && Containers.Count == 0;

    public string NoContainersMessage => _all.Count == 0
        ? "There are no containers on this server."
        : "No container matches the filter.";

    public string NoConnectionMessage => HasConnectionChoices
        ? "Choose a connection above and select Connect to see its containers."
        : "No SSH connections are saved yet. Create one on the Connections page first.";

    /// <summary>Reloads the picker from the saved connections, keeping whatever was selected if it survived.</summary>
    [RelayCommand]
    public async Task LoadConnectionsAsync(CancellationToken cancellationToken = default)
    {
        if (connectionQueries is null)
        {
            return;
        }

        try
        {
            var items = await connectionQueries.QueryAsync(
                new ConnectionFilter
                {
                    Protocols = [ProtocolType.Ssh, ProtocolType.Sftp],
                    SortBy = ConnectionSortBy.Name,
                },
                cancellationToken).ConfigureAwait(true);
            var previous = SelectedConnection?.Id ?? _attachedConnectionId;
            AvailableConnections.Clear();
            foreach (var item in items)
            {
                AvailableConnections.Add(new DockerConnectionChoice(item.Id, item.Name, $"{item.Host}:{item.Port}"));
            }

            SelectedConnection = AvailableConnections.FirstOrDefault(choice => choice.Id == previous);
            NotifyConnectionChoicesChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ErrorMessage = $"The connection list could not be loaded: {exception.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanConnectSelected))]
    public Task ConnectSelectedAsync(CancellationToken cancellationToken = default)
    {
        return SelectedConnection is { } choice
            ? AttachAsync(choice.Id, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Connects to the server, checks that Docker answers, and lists its containers. A server
    /// without Docker, or an account that may not use it, is reported here — once, in words — rather than
    /// as a failed refresh every five seconds.</summary>
    public async Task AttachAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        FeedbackMessage = null;
        try
        {
            var next = await _sessions.OpenAsync(connectionId, cancellationToken).ConfigureAwait(true);
            await DisposeSessionAsync().ConfigureAwait(true);
            _attachedConnectionId = connectionId;
            ConnectionTitle = next.Definition.Name;
            SyncPickerWithSession(next.Definition.Id, next.Definition.Name, $"{next.Definition.Host}:{next.Definition.Port}");

            var probe = await next.Docker.ProbeAsync(cancellationToken).ConfigureAwait(true);
            if (probe.IsFailure)
            {
                await next.DisposeAsync().ConfigureAwait(true);
                ErrorMessage = probe.Failure.Message;
                return;
            }

            _session = next;
            ServerVersion = string.IsNullOrWhiteSpace(probe.Value.ServerVersion)
                ? null
                : $"Docker {probe.Value.ServerVersion}";
            OnPropertyChanged(nameof(IsConnected));
            IsLoading = false;
            await RefreshCoreAsync(includeStats: true, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ErrorMessage = "The connection was cancelled.";
        }
        catch (Exception exception)
        {
            ErrorMessage = $"The Docker page could not connect: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(HasNoContainers));
        }
    }

    [RelayCommand]
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return RefreshCoreAsync(includeStats: true, cancellationToken);
    }

    [RelayCommand]
    public Task StartContainerAsync(DockerContainerItemViewModel? item)
    {
        return RunActionAsync(item, DockerAction.Start);
    }

    [RelayCommand]
    public Task StopContainerAsync(DockerContainerItemViewModel? item)
    {
        return RunActionAsync(item, DockerAction.Stop);
    }

    [RelayCommand]
    public Task RestartContainerAsync(DockerContainerItemViewModel? item)
    {
        return RunActionAsync(item, DockerAction.Restart);
    }

    [RelayCommand]
    public Task RemoveContainerAsync(DockerContainerItemViewModel? item)
    {
        return RunActionAsync(item, DockerAction.Remove);
    }

    [RelayCommand]
    public async Task ShowLogsAsync(DockerContainerItemViewModel? item)
    {
        item ??= SelectedContainer;
        if (item is null || _session is null)
        {
            return;
        }

        await Logs.OpenAsync(_session.Docker, item.Id, item.Name).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task OpenShellAsync(DockerContainerItemViewModel? item)
    {
        item ??= SelectedContainer;
        if (item is null || _session is null || shells is null)
        {
            return;
        }

        try
        {
            await shells.OpenAsync(_session.Definition.Id, _session.Definition.Name, item.Id, item.Name)
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ErrorMessage = $"A shell in '{item.Name}' could not be opened: {exception.Message}";
        }
    }

    [RelayCommand]
    public async Task CopyIdAsync(DockerContainerItemViewModel? item)
    {
        item ??= SelectedContainer;
        if (item is null || clipboard is null)
        {
            return;
        }

        var result = await clipboard.WriteTextAsync(item.Id).ConfigureAwait(true);
        FeedbackMessage = result.Succeeded ? $"Copied the ID of {item.Name}." : result.ErrorMessage;
    }

    /// <summary>The page came on screen: start the periodic refresh.</summary>
    public void Activate()
    {
        if (_autoRefresh is not null)
        {
            return;
        }

        _autoRefresh = new CancellationTokenSource();
        _ = AutoRefreshLoopAsync(_autoRefresh.Token);
    }

    /// <summary>The page left the screen: nobody is looking, so stop polling the server. Logs keep
    /// following; they are cheap and the tail would otherwise have a hole in it.</summary>
    public void Deactivate()
    {
        _autoRefresh?.Cancel();
        _autoRefresh?.Dispose();
        _autoRefresh = null;
    }

    public async ValueTask DisposeAsync()
    {
        Deactivate();
        await Logs.DisposeAsync().ConfigureAwait(false);
        await DisposeSessionAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    partial void OnFilterTextChanged(string value)
    {
        ApplyFilter();
    }

    partial void OnShowStoppedChanged(bool value)
    {
        ApplyFilter();
    }

    private async Task AutoRefreshLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(AutoRefreshInterval, cancellationToken).ConfigureAwait(true);
                if (AutoRefresh && IsConnected)
                {
                    await RefreshCoreAsync(includeStats: true, cancellationToken).ConfigureAwait(true);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshCoreAsync(bool includeStats, CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null || Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return;
        }

        try
        {
            var listed = await session.Docker.ListContainersAsync(cancellationToken).ConfigureAwait(true);
            if (!ReferenceEquals(session, _session))
            {
                return;
            }

            if (listed.IsFailure)
            {
                ErrorMessage = listed.Failure.Message;
                return;
            }

            ErrorMessage = null;
            Merge(listed.Value);
            if (!includeStats || !_all.Any(item => item.IsRunning))
            {
                return;
            }

            var stats = await session.Docker.GetStatsAsync(cancellationToken).ConfigureAwait(true);
            if (stats.IsSuccess && ReferenceEquals(session, _session))
            {
                ApplyStats(stats.Value);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _ = Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private async Task RunActionAsync(DockerContainerItemViewModel? item, DockerAction action)
    {
        item ??= SelectedContainer;
        var session = _session;
        if (item is null || session is null || item.IsBusy)
        {
            return;
        }

        if (!await ConfirmAsync(item, action, session).ConfigureAwait(true))
        {
            return;
        }

        item.IsBusy = true;
        ErrorMessage = null;
        FeedbackMessage = $"{Progressive(action)} {item.Name}…";
        try
        {
            var result = await session.Docker.RunActionAsync(item.Id, action).ConfigureAwait(true);
            if (result.IsFailure)
            {
                FeedbackMessage = null;
                ErrorMessage = $"'{item.Name}' could not be {Past(action)}: {result.Failure.Message}";
                return;
            }

            FeedbackMessage = $"{Capitalized(Past(action))} {item.Name}.";
        }
        finally
        {
            item.IsBusy = false;
        }

        await RefreshCoreAsync(includeStats: action != DockerAction.Remove, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Removing always asks. Stopping and restarting ask only on a connection marked production,
    /// where a stray click is an outage; anywhere else the extra dialog would just be in the way.</summary>
    private Task<bool> ConfirmAsync(DockerContainerItemViewModel item, DockerAction action, DockerWorkspaceSession session)
    {
        var isProduction = session.Definition.Environment == EnvironmentKind.Production;
        return action switch
        {
            DockerAction.Remove => _confirmation.ConfirmAsync(
                "Remove container",
                $"Remove the container '{item.Name}' from {session.Definition.Name}? Its writable layer is deleted; " +
                "named volumes and the image are kept.",
                "Remove"),
            DockerAction.Stop => isProduction
                ? _confirmation.ConfirmAsync(
                    "Stop container",
                    $"{session.Definition.Name} is marked as production. Stop '{item.Name}'?",
                    "Stop")
                : Task.FromResult(true),
            DockerAction.Restart => isProduction
                ? _confirmation.ConfirmAsync(
                    "Restart container",
                    $"{session.Definition.Name} is marked as production. Restart '{item.Name}'?",
                    "Restart")
                : Task.FromResult(true),
            DockerAction.Start => Task.FromResult(true),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private void Merge(IReadOnlyList<DockerContainer> containers)
    {
        var byId = _all.ToDictionary(item => item.Id, StringComparer.Ordinal);
        _all.Clear();
        foreach (var container in containers)
        {
            if (byId.TryGetValue(container.Id, out var existing))
            {
                existing.Update(container);
                _all.Add(existing);
            }
            else
            {
                _all.Add(new DockerContainerItemViewModel(container));
            }
        }

        ApplyFilter();
    }

    private void ApplyStats(IReadOnlyList<DockerContainerStats> stats)
    {
        foreach (var item in _all)
        {
            item.ApplyStats(stats.FirstOrDefault(sample =>
                string.Equals(sample.Name, item.Name, StringComparison.Ordinal)
                || (sample.Id.Length > 0 && (item.Id.StartsWith(sample.Id, StringComparison.Ordinal)
                    || sample.Id.StartsWith(item.Id, StringComparison.Ordinal)))));
        }
    }

    private void ApplyFilter()
    {
        var filter = FilterText.Trim();
        var visible = _all
            .Where(item => ShowStopped || item.IsRunning)
            .Where(item => filter.Length == 0
                || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || item.Image.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || item.Id.StartsWith(filter, StringComparison.OrdinalIgnoreCase)
                || (item.Container.ComposeProject?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(item => item.Container.ComposeProject is null ? 1 : 0)
            .ThenBy(item => item.Container.ComposeProject, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Move rows rather than rebuilding the list: a Clear would drop the selection on every refresh.
        for (var index = Containers.Count - 1; index >= 0; index--)
        {
            if (!visible.Contains(Containers[index]))
            {
                Containers.RemoveAt(index);
            }
        }

        for (var index = 0; index < visible.Count; index++)
        {
            var current = Containers.IndexOf(visible[index]);
            if (current < 0)
            {
                Containers.Insert(index, visible[index]);
            }
            else if (current != index)
            {
                Containers.Move(current, index);
            }
        }

        var running = _all.Count(item => item.IsRunning);
        Summary = _all.Count == 0
            ? string.Empty
            : $"{_all.Count} {(_all.Count == 1 ? "container" : "containers")}, {running} running";
        OnPropertyChanged(nameof(HasNoContainers));
        OnPropertyChanged(nameof(NoContainersMessage));
    }

    private async Task DisposeSessionAsync()
    {
        await Logs.CloseAsync().ConfigureAwait(false);
        _all.Clear();
        Containers.Clear();
        Summary = string.Empty;
        ServerVersion = null;
        if (_session is not null)
        {
            var session = _session;
            _session = null;
            await session.DisposeAsync().ConfigureAwait(false);
        }

        OnPropertyChanged(nameof(IsConnected));
    }

    private bool CanConnectSelected()
    {
        return SelectedConnection is not null;
    }

    private void NotifyConnectionChoicesChanged()
    {
        OnPropertyChanged(nameof(HasConnectionChoices));
        OnPropertyChanged(nameof(NoConnectionMessage));
    }

    private void SyncPickerWithSession(Guid connectionId, string name, string endpoint)
    {
        var match = AvailableConnections.FirstOrDefault(choice => choice.Id == connectionId);
        if (match is null)
        {
            match = new DockerConnectionChoice(connectionId, name, endpoint);
            AvailableConnections.Add(match);
            NotifyConnectionChoicesChanged();
        }

        SelectedConnection = match;
    }

    private static string Progressive(DockerAction action)
    {
        return action switch
        {
            DockerAction.Start => "Starting",
            DockerAction.Stop => "Stopping",
            DockerAction.Restart => "Restarting",
            DockerAction.Remove => "Removing",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static string Past(DockerAction action)
    {
        return action switch
        {
            DockerAction.Start => "started",
            DockerAction.Stop => "stopped",
            DockerAction.Restart => "restarted",
            DockerAction.Remove => "removed",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    private static string Capitalized(string value)
    {
        return value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
    }
}
