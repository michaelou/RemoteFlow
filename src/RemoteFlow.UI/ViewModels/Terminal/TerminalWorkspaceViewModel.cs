using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Settings;

namespace RemoteFlow.UI.ViewModels.Terminal;

public class TerminalWorkspaceViewModel : PageViewModel, IAsyncDisposable, IDisposable
{
    private const int _minimumGridColumns = 1;
    private const int _maximumGridColumns = 6;

    private readonly IPtyService? _ptyService;
    private readonly IUiDispatcher? _dispatcher;
    private readonly ISettingsStore? _settings;
    private readonly IConfirmationDialogService? _confirmation;
    private readonly TerminalSettingsViewModel? _terminalSettings;
    private readonly IShellProfileService? _shellProfileService;
    private readonly ISystemTerminalLauncher? _systemTerminalLauncher;
    private readonly ISessionManager? _sessionManager;
    private int _startingCount;
    private int _disposeStarted;
    private bool _isGridLayout;
    private int _maxGridColumns = 3;
    private bool _layoutSettingsLoaded;
    private Task _pendingLayoutSave = Task.CompletedTask;

    public TerminalWorkspaceViewModel()
        : base("Terminals")
    {
        Sessions.CollectionChanged += OnSessionsChanged;
    }

    public TerminalWorkspaceViewModel(IPtyService ptyService, IUiDispatcher dispatcher)
        : this(ptyService, dispatcher, null, null, null, null)
    {
    }

    public TerminalWorkspaceViewModel(
        IPtyService ptyService,
        IUiDispatcher dispatcher,
        ISettingsStore? settings,
        IConfirmationDialogService? confirmation,
        KeymapService? keymap,
        TerminalClipboardController? clipboardController)
        : this(ptyService, dispatcher, settings, confirmation, keymap, clipboardController, null)
    {
    }

    public TerminalWorkspaceViewModel(
        IPtyService ptyService,
        IUiDispatcher dispatcher,
        ISettingsStore? settings,
        IConfirmationDialogService? confirmation,
        KeymapService? keymap,
        TerminalClipboardController? clipboardController,
        TerminalSettingsViewModel? terminalSettings)
        : this(
            ptyService,
            dispatcher,
            settings,
            confirmation,
            keymap,
            clipboardController,
            terminalSettings,
            null,
            null)
    {
    }

    public TerminalWorkspaceViewModel(
        IPtyService ptyService,
        IUiDispatcher dispatcher,
        ISettingsStore? settings,
        IConfirmationDialogService? confirmation,
        KeymapService? keymap,
        TerminalClipboardController? clipboardController,
        TerminalSettingsViewModel? terminalSettings,
        IShellProfileService? shellProfileService,
        ISystemTerminalLauncher? systemTerminalLauncher,
        ISessionManager? sessionManager = null)
        : base("Terminals")
    {
        _ptyService = ptyService ?? throw new ArgumentNullException(nameof(ptyService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _settings = settings;
        _confirmation = confirmation;
        Keymap = keymap ?? new KeymapService();
        ClipboardController = clipboardController;
        _terminalSettings = terminalSettings;
        _shellProfileService = shellProfileService;
        _systemTerminalLauncher = systemTerminalLauncher;
        _sessionManager = sessionManager;
        Sessions.CollectionChanged += OnSessionsChanged;
        if (_shellProfileService is { } activeProfileService)
        {
            activeProfileService.ProfilesChanged += OnShellProfilesChanged;
        }
        if (_terminalSettings is { } activeSettings)
        {
            activeSettings.SettingsChanged += OnTerminalSettingsChanged;
        }
        if (_sessionManager is { } activeSessionManager)
        {
            activeSessionManager.SessionAdded += OnManagedSessionAdded;
            activeSessionManager.SessionChanged += OnManagedSessionChanged;
            activeSessionManager.SessionRemoved += OnManagedSessionRemoved;
        }
    }

    public ObservableCollection<IWorkspaceSessionViewModel> Sessions { get; } = [];

    public ObservableCollection<ShellProfileMenuItemViewModel> ShellProfiles { get; } = [];

    public KeymapService Keymap { get; } = new();

    public TerminalClipboardController? ClipboardController { get; }

    public IWorkspaceSessionViewModel? SelectedSession { get; private set; }

    public TerminalSessionViewModel? SelectedTerminalSession => SelectedSession as TerminalSessionViewModel;

    public TerminalSessionViewModel? Session => SelectedTerminalSession;

    public bool IsStarting => Volatile.Read(ref _startingCount) > 0;

    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Whether every session is on screen at once rather than one at a time.
    /// </summary>
    /// <remarks>
    /// The sessions themselves carry the flag, because the content of all of them is always attached and
    /// realized — an embedded remote desktop owns a native window that cannot survive being re-hosted — so
    /// the only thing a layout change may do is decide which of them is visible.
    /// </remarks>
    public bool IsGridLayout
    {
        get => _isGridLayout;
        set
        {
            if (_isGridLayout == value)
            {
                return;
            }

            _isGridLayout = value;
            ApplyLayoutToSessions();
            OnPropertyChanged();
            PersistLayoutSettings();
        }
    }

    /// <summary>How many tiles a grid row may hold before it starts a new row.</summary>
    public int MaxGridColumns
    {
        get => _maxGridColumns;
        set
        {
            var columns = Math.Clamp(value, _minimumGridColumns, _maximumGridColumns);
            if (_maxGridColumns == columns)
            {
                return;
            }

            _maxGridColumns = columns;
            OnPropertyChanged();
            PersistLayoutSettings();
        }
    }

    public IReadOnlyList<int> GridColumnOptions { get; } =
        [.. Enumerable.Range(_minimumGridColumns, _maximumGridColumns - _minimumGridColumns + 1)];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await LoadLayoutSettingsAsync(cancellationToken).ConfigureAwait(true);
        _ = await GetCtrlCPolicyAsync(cancellationToken).ConfigureAwait(true);
        await LoadShellProfilesAsync(cancellationToken).ConfigureAwait(true);
        if (Sessions.Count == 0)
        {
            _ = await AddLocalSessionAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task<TerminalSessionViewModel?> AddLocalSessionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_shellProfileService is not null)
        {
            var profile = await _shellProfileService.GetDefaultProfileAsync(cancellationToken).ConfigureAwait(true);
            return await AddProfileSessionAsync(profile, cancellationToken).ConfigureAwait(true);
        }

        return await AddSessionAsync(
            CreateDefaultShellOptions(),
            "Local shell",
            EnvironmentKind.Unspecified,
            colorOverrideHex: null,
            cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    public Task<TerminalSessionViewModel?> AddProfileSessionAsync(
        ShellProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            var options = _shellProfileService?.CreateSpawnOptions(profile) ?? new PtySpawnOptions
            {
                ShellPath = profile.ShellPath,
                Arguments = profile.Arguments,
                WorkingDirectory = profile.WorkingDirectory,
                EnvironmentVariables = profile.EnvironmentVariables,
            };
            return AddSessionAsync(
                options,
                profile.DisplayName,
                EnvironmentKind.Unspecified,
                colorOverrideHex: null,
                profile,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromResult<TerminalSessionViewModel?>(AddFailedSession(profile, exception.Message));
        }
    }

    public async Task<TerminalSessionViewModel?> AddSessionAsync(
        PtySpawnOptions options,
        string title,
        EnvironmentKind environment,
        string? colorOverrideHex,
        ShellProfile? shellProfile = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (_ptyService is null || _dispatcher is null)
        {
            SetError("Local terminal services are unavailable in preview mode.");
            return null;
        }

        _ = Interlocked.Increment(ref _startingCount);
        OnPropertyChanged(nameof(IsStarting));
        SetError(null);
        try
        {
            if (_terminalSettings is not null)
            {
                await _terminalSettings.InitializeAsync(cancellationToken).ConfigureAwait(true);
            }

            var channel = await _ptyService.SpawnAsync(options, cancellationToken).ConfigureAwait(true);
            var session = new TerminalSessionViewModel(
                channel,
                _dispatcher,
                initialTitle: title,
                environment: environment,
                colorOverrideHex: colorOverrideHex,
                shellProfile: shellProfile);
            if (_terminalSettings is not null)
            {
                session.ApplyAppearance(_terminalSettings.Current);
            }
            Sessions.Add(session);
            ClipboardController?.Attach(session, SetError);
            SelectSession(session);
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return AddFailedSession(shellProfile, $"The local shell could not be started: {exception.Message}", title);
        }
        finally
        {
            _ = Interlocked.Decrement(ref _startingCount);
            OnPropertyChanged(nameof(IsStarting));
        }
    }

    public void AddWorkspaceSession(IWorkspaceSessionViewModel session)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        ArgumentNullException.ThrowIfNull(session);
        if (Sessions.Contains(session))
        {
            return;
        }

        Sessions.Add(session);
        if (session is IWorkspaceSessionCloseRequestSource closeSource)
        {
            closeSource.CloseRequested += OnSessionCloseRequested;
        }
        SelectSession(session);
    }

    public void SelectSession(IWorkspaceSessionViewModel? session)
    {
        if (session is not null && !Sessions.Contains(session))
        {
            throw new ArgumentException("The session does not belong to this workspace.", nameof(session));
        }

        if (ReferenceEquals(SelectedSession, session))
        {
            return;
        }

        var previous = SelectedSession;
        SetActive(previous, false);

        SelectedSession = session;
        var current = SelectedSession;
        SetActive(current, true);

        OnPropertyChanged(nameof(SelectedSession));
        OnPropertyChanged(nameof(SelectedTerminalSession));
        OnPropertyChanged(nameof(Session));
    }

    public void SelectSession(int oneBasedIndex)
    {
        if (oneBasedIndex is < 1 or > 9 || oneBasedIndex > Sessions.Count)
        {
            return;
        }

        SelectSession(Sessions[oneBasedIndex - 1]);
    }

    public void CycleSession(bool backwards = false)
    {
        if (Sessions.Count < 2)
        {
            return;
        }

        var current = SelectedSession is null ? -1 : Sessions.IndexOf(SelectedSession);
        var next = backwards
            ? (current <= 0 ? Sessions.Count : current) - 1
            : (current + 1) % Sessions.Count;
        SelectSession(Sessions[next]);
    }

    public void MoveSession(IWorkspaceSessionViewModel session, IWorkspaceSessionViewModel beforeSession)
    {
        var oldIndex = Sessions.IndexOf(session);
        var newIndex = Sessions.IndexOf(beforeSession);
        if (oldIndex < 0 || newIndex < 0 || oldIndex == newIndex)
        {
            return;
        }

        Sessions.Move(oldIndex, newIndex);
    }

    public async Task<bool> CloseSessionAsync(
        IWorkspaceSessionViewModel session,
        bool skipConfirmation = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var index = Sessions.IndexOf(session);
        if (index < 0)
        {
            return true;
        }

        if (!skipConfirmation && session.IsLive && await ShouldConfirmActiveSessionCloseAsync(cancellationToken).ConfigureAwait(true))
        {
            var isTerminal = session is TerminalSessionViewModel;
            if (_confirmation is null || !await _confirmation.ConfirmAsync(
                    isTerminal ? "Close active terminal?" : "Close active remote desktop?",
                    isTerminal
                        ? $"'{session.Title}' is still running. Closing it will terminate the process."
                        : $"'{session.Title}' is still connected. Closing it will disconnect the remote desktop.",
                    isTerminal ? "Close terminal" : "Close remote desktop",
                    cancellationToken).ConfigureAwait(true))
            {
                return false;
            }
        }

        var wasSelected = ReferenceEquals(SelectedSession, session);
        if (session is TerminalSessionViewModel terminal)
        {
            ClipboardController?.Detach(terminal);
        }
        UnsubscribeCloseRequest(session);
        Sessions.RemoveAt(index);
        if (wasSelected)
        {
            SelectSession(Sessions.Count == 0 ? null : Sessions[Math.Min(index, Sessions.Count - 1)]);
        }

        await session.DisposeAsync().ConfigureAwait(true);
        return true;
    }

    public async Task<bool> RequestCloseAllAsync(CancellationToken cancellationToken = default)
    {
        var live = Sessions.Where(session => session.IsLive).ToArray();
        if (live.Length > 0 && await ShouldConfirmActiveSessionCloseAsync(cancellationToken).ConfigureAwait(true))
        {
            var names = string.Join(Environment.NewLine, live.Select(session => $"• {session.Title}"));
            var terminalsOnly = live.All(session => session is TerminalSessionViewModel);
            if (_confirmation is null || !await _confirmation.ConfirmAsync(
                    terminalsOnly ? "Close active terminals?" : "Close active sessions?",
                    $"The following sessions are still running:{Environment.NewLine}{Environment.NewLine}{names}",
                    terminalsOnly ? "Close all terminals" : "Close all sessions",
                    cancellationToken).ConfigureAwait(true))
            {
                return false;
            }
        }

        await DisposeSessionsAsync().ConfigureAwait(true);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        await DisposeSessionsAsync().ConfigureAwait(false);
        if (_terminalSettings is { } terminalSettings)
        {
            terminalSettings.SettingsChanged -= OnTerminalSettingsChanged;
        }
        if (_shellProfileService is { } profileService)
        {
            profileService.ProfilesChanged -= OnShellProfilesChanged;
        }
        if (_sessionManager is { } sessionManager)
        {
            sessionManager.SessionAdded -= OnManagedSessionAdded;
            sessionManager.SessionChanged -= OnManagedSessionChanged;
            sessionManager.SessionRemoved -= OnManagedSessionRemoved;
        }
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    protected static PtySpawnOptions CreateDefaultShellOptions()
    {
        var shell = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe")
            : Environment.GetEnvironmentVariable("SHELL") ?? (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh");
        // A POSIX shell is given nothing: on a PTY it is interactive and reads the user's own configuration,
        // which is where the prompt, the aliases and dircolors live. Suppressing that was why the Linux
        // terminal opened on a bare uncoloured prompt while PowerShell on Windows looked right.
        IReadOnlyList<string> arguments = OperatingSystem.IsWindows() ? ["/Q", "/D", "/K"] : [];
        return new PtySpawnOptions
        {
            ShellPath = shell,
            Arguments = arguments,
            WorkingDirectory = Environment.CurrentDirectory,
            EnvironmentVariables = new Dictionary<string, string>
            {
                ["TERM"] = "xterm-256color",
                ["COLORTERM"] = "truecolor",
            },
        };
    }

    private async Task<bool> ShouldConfirmActiveSessionCloseAsync(CancellationToken cancellationToken)
    {
        return _settings is null ||
            await _settings.Get(SettingKeys.ConfirmCloseActiveSession, cancellationToken).ConfigureAwait(true);
    }

    private async Task DisposeSessionsAsync()
    {
        var sessions = Sessions.ToArray();
        foreach (var session in sessions)
        {
            if (session is TerminalSessionViewModel terminal)
            {
                ClipboardController?.Detach(terminal);
            }
            UnsubscribeCloseRequest(session);
        }

        Sessions.Clear();
        SelectSession(null);
        await Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private void SetError(string? message)
    {
        if (string.Equals(ErrorMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        ErrorMessage = message;
        OnPropertyChanged(nameof(ErrorMessage));
    }

    public void ReportError(string? message)
    {
        SetError(message);
    }

    public async Task OpenInSystemTerminalAsync(
        TerminalSessionViewModel session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_systemTerminalLauncher is null || session.ShellProfile is null)
        {
            SetError("This session does not have a local shell profile that can be opened in the system terminal.");
            return;
        }

        var result = await _systemTerminalLauncher.OpenLocalAsync(session.ShellProfile, cancellationToken).ConfigureAwait(true);
        SetError(result.ErrorMessage);
    }

    /// <summary>
    /// The Ctrl+C policy last read from settings.
    /// </summary>
    /// <remarks>
    /// Key routing has to decide whether a stroke belongs to the app before the key event reaches
    /// <c>TerminalControl</c>, so the policy is cached rather than awaited per keystroke.
    /// </remarks>
    public CtrlCPolicy CtrlCPolicy { get; private set; } = CtrlCPolicy.SigintAlways;

    public async Task<CtrlCPolicy> GetCtrlCPolicyAsync(CancellationToken cancellationToken = default)
    {
        if (_settings is null)
        {
            return CtrlCPolicy;
        }

        CtrlCPolicy = await _settings.Get(SettingKeys.CtrlCPolicy, cancellationToken).ConfigureAwait(true);
        return CtrlCPolicy;
    }

    /// <summary>Awaits the settings writes the layout controls started, so a test does not race them.</summary>
    public async Task FlushLayoutSettingsAsync()
    {
        await _pendingLayoutSave.ConfigureAwait(false);
    }

    private async Task LoadLayoutSettingsAsync(CancellationToken cancellationToken)
    {
        if (_layoutSettingsLoaded)
        {
            return;
        }

        if (_settings is not null)
        {
            var layout = await _settings.Get(SettingKeys.WorkspaceLayout, cancellationToken).ConfigureAwait(true);
            var columns = await _settings.Get(SettingKeys.WorkspaceGridMaxColumns, cancellationToken)
                .ConfigureAwait(true);
            _maxGridColumns = Math.Clamp(columns, _minimumGridColumns, _maximumGridColumns);
            _isGridLayout = layout == WorkspaceLayoutMode.Grid;
            ApplyLayoutToSessions();
            OnPropertyChanged(nameof(MaxGridColumns));
            OnPropertyChanged(nameof(IsGridLayout));
        }

        // Set last: until the stored layout has been read, a property change is the load itself and must
        // not be written back.
        _layoutSettingsLoaded = true;
    }

    private void PersistLayoutSettings()
    {
        if (!_layoutSettingsLoaded || _settings is null)
        {
            return;
        }

        _pendingLayoutSave = SaveLayoutSettingsAsync(
            _pendingLayoutSave,
            _isGridLayout ? WorkspaceLayoutMode.Grid : WorkspaceLayoutMode.Tabs,
            _maxGridColumns);
    }

    private async Task SaveLayoutSettingsAsync(Task previousSave, WorkspaceLayoutMode layout, int columns)
    {
        await previousSave.ConfigureAwait(false);
        await _settings!.Set(SettingKeys.WorkspaceLayout, layout).ConfigureAwait(false);
        await _settings.Set(SettingKeys.WorkspaceGridMaxColumns, columns).ConfigureAwait(false);
    }

    private void ApplyLayoutToSessions()
    {
        foreach (var session in Sessions)
        {
            session.SetTiled(_isGridLayout);
        }
    }

    /// <summary>Sessions arrive from four places; the collection is the one point they all pass through.</summary>
    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null)
        {
            return;
        }

        foreach (var session in e.NewItems.OfType<IWorkspaceSessionViewModel>())
        {
            session.SetTiled(_isGridLayout);
        }
    }

    private static void SetActive(IWorkspaceSessionViewModel? session, bool isActive)
    {
        session?.SetActive(isActive);
    }

    private void OnTerminalSettingsChanged(object? sender, EventArgs e)
    {
        if (_terminalSettings is null)
        {
            return;
        }

        foreach (var session in Sessions.OfType<TerminalSessionViewModel>())
        {
            session.ApplyAppearance(_terminalSettings.Current);
        }
    }

    private async Task LoadShellProfilesAsync(CancellationToken cancellationToken, bool force = false)
    {
        if (_shellProfileService is null || (!force && ShellProfiles.Count > 0))
        {
            return;
        }

        var profiles = await _shellProfileService.GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        void UpdateProfiles()
        {
            ShellProfiles.Clear();
            foreach (var profile in profiles)
            {
                ShellProfiles.Add(new ShellProfileMenuItemViewModel(
                    profile,
                    () => AddProfileSessionAsync(profile, CancellationToken.None)));
            }
        }

        if (_dispatcher is null)
        {
            UpdateProfiles();
        }
        else
        {
            await _dispatcher.InvokeAsync(UpdateProfiles, cancellationToken).ConfigureAwait(false);
        }
    }

    private async void OnShellProfilesChanged(object? sender, EventArgs e)
    {
        try
        {
            await LoadShellProfilesAsync(CancellationToken.None, force: true).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            SetError($"Shell profiles could not be refreshed: {exception.Message}");
        }
    }

    private TerminalSessionViewModel AddFailedSession(
        ShellProfile? profile,
        string message,
        string? title = null)
    {
        if (_dispatcher is null)
        {
            SetError(message);
            return null!;
        }

        var failed = TerminalSessionViewModel.CreateFailed(
            _dispatcher,
            title ?? profile?.DisplayName ?? "Shell failed",
            message,
            profile);
        Sessions.Add(failed);
        SelectSession(failed);
        SetError(null);
        return failed;
    }

    private void OnManagedSessionAdded(object? sender, ManagedSshSession session)
    {
        _ = AddManagedSessionAsync(session);
    }

    private async Task AddManagedSessionAsync(ManagedSshSession managed)
    {
        if (_dispatcher is null)
        {
            return;
        }
        await _dispatcher.InvokeAsync(() =>
        {
            if (Sessions.OfType<TerminalSessionViewModel>().Any(item => item.ManagedSessionId == managed.SessionId))
            {
                return;
            }
            var session = new TerminalSessionViewModel(
                managed.Channel,
                _dispatcher,
                initialTitle: managed.Title,
                environment: managed.Environment,
                colorOverrideHex: managed.ColorOverrideHex,
                managedSessionId: managed.SessionId,
                initialState: managed.State,
                retry: token => _sessionManager!.RetryAsync(managed.SessionId, token),
                close: token => _sessionManager!.CloseAsync(managed.SessionId, token));
            session.ApplyManagedState(managed.State, managed.FailureReason);
            if (_terminalSettings is not null)
            {
                session.ApplyAppearance(_terminalSettings.Current);
            }
            Sessions.Add(session);
            ClipboardController?.Attach(session, SetError);
            SelectSession(session);
        }).ConfigureAwait(false);
    }

    private void OnManagedSessionChanged(object? sender, SessionTransitionEventArgs e)
    {
        _ = ApplyManagedSessionChangeAsync(e);
    }

    private async Task ApplyManagedSessionChangeAsync(SessionTransitionEventArgs e)
    {
        if (_dispatcher is null)
        {
            return;
        }
        await _dispatcher.InvokeAsync(() =>
        {
            var session = Sessions.OfType<TerminalSessionViewModel>()
                .FirstOrDefault(item => item.ManagedSessionId == e.Session.SessionId);
            session?.ApplyManagedState(e.CurrentState, e.Session.FailureReason);
        }).ConfigureAwait(false);
    }

    private void OnManagedSessionRemoved(object? sender, ManagedSshSession session)
    {
        _ = RemoveManagedSessionAsync(session.SessionId);
    }

    private async Task RemoveManagedSessionAsync(Guid sessionId)
    {
        if (_dispatcher is null)
        {
            return;
        }
        await _dispatcher.InvokeAsync(() =>
        {
            var session = Sessions.OfType<TerminalSessionViewModel>()
                .FirstOrDefault(item => item.ManagedSessionId == sessionId);
            if (session is null)
            {
                return;
            }
            var index = Sessions.IndexOf(session);
            var wasSelected = ReferenceEquals(SelectedSession, session);
            ClipboardController?.Detach(session);
            _ = Sessions.Remove(session);
            if (wasSelected)
            {
                SelectSession(Sessions.Count == 0 ? null : Sessions[Math.Min(index, Sessions.Count - 1)]);
            }
        }).ConfigureAwait(false);
    }

    private async void OnSessionCloseRequested(object? sender, EventArgs e)
    {
        if (sender is IWorkspaceSessionViewModel session)
        {
            _ = await CloseSessionAsync(session).ConfigureAwait(true);
        }
    }

    private void UnsubscribeCloseRequest(IWorkspaceSessionViewModel session)
    {
        if (session is IWorkspaceSessionCloseRequestSource closeSource)
        {
            closeSource.CloseRequested -= OnSessionCloseRequested;
        }
    }
}

public sealed class ShellProfileMenuItemViewModel(
    ShellProfile profile,
    Func<Task<TerminalSessionViewModel?>> open)
{
    public ShellProfile Profile { get; } = profile;

    public string DisplayName => Profile.DisplayName;

    public string Icon => Profile.Icon;

    public IAsyncRelayCommand OpenCommand { get; } = new AsyncRelayCommand(async () =>
    {
        _ = await open().ConfigureAwait(true);
    });
}
