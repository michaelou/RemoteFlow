using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Settings;

namespace RemoteFlow.UI.ViewModels.Terminal;

public class TerminalWorkspaceViewModel : PageViewModel, IAsyncDisposable, IDisposable
{
    private readonly IPtyService? _ptyService;
    private readonly IUiDispatcher? _dispatcher;
    private readonly ISettingsStore? _settings;
    private readonly IConfirmationDialogService? _confirmation;
    private readonly TerminalSettingsViewModel? _terminalSettings;
    private readonly IShellProfileService? _shellProfileService;
    private readonly ISystemTerminalLauncher? _systemTerminalLauncher;
    private int _startingCount;
    private int _disposeStarted;

    public TerminalWorkspaceViewModel()
        : base("Terminals")
    {
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
        ISystemTerminalLauncher? systemTerminalLauncher)
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
        if (_shellProfileService is { } activeProfileService)
        {
            activeProfileService.ProfilesChanged += OnShellProfilesChanged;
        }
        if (_terminalSettings is { } activeSettings)
        {
            activeSettings.SettingsChanged += OnTerminalSettingsChanged;
        }
    }

    public ObservableCollection<TerminalSessionViewModel> Sessions { get; } = [];

    public ObservableCollection<ShellProfileMenuItemViewModel> ShellProfiles { get; } = [];

    public KeymapService Keymap { get; } = new();

    public TerminalClipboardController? ClipboardController { get; }

    public TerminalSessionViewModel? SelectedSession { get; private set; }

    public TerminalSessionViewModel? Session => SelectedSession;

    public bool IsStarting => Volatile.Read(ref _startingCount) > 0;

    public string? ErrorMessage { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
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

    public void SelectSession(TerminalSessionViewModel? session)
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

    public void MoveSession(TerminalSessionViewModel session, TerminalSessionViewModel beforeSession)
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
        TerminalSessionViewModel session,
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
            if (_confirmation is null || !await _confirmation.ConfirmAsync(
                    "Close active terminal?",
                    $"'{session.Title}' is still running. Closing it will terminate the process.",
                    "Close terminal",
                    cancellationToken).ConfigureAwait(true))
            {
                return false;
            }
        }

        var wasSelected = ReferenceEquals(SelectedSession, session);
        ClipboardController?.Detach(session);
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
            if (_confirmation is null || !await _confirmation.ConfirmAsync(
                    "Close active terminals?",
                    $"The following sessions are still running:{Environment.NewLine}{Environment.NewLine}{names}",
                    "Close all terminals",
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
        IReadOnlyList<string> arguments = OperatingSystem.IsWindows()
            ? ["/Q", "/D", "/K"]
            : Path.GetFileName(shell).Equals("bash", StringComparison.OrdinalIgnoreCase)
                ? ["--noprofile", "--norc"]
                : [];
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
            ClipboardController?.Detach(session);
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

    public Task<CtrlCPolicy> GetCtrlCPolicyAsync(CancellationToken cancellationToken = default)
    {
        return _settings?.Get(SettingKeys.CtrlCPolicy, cancellationToken) ??
            Task.FromResult(CtrlCPolicy.SigintAlways);
    }

    private static void SetActive(TerminalSessionViewModel? session, bool isActive)
    {
        session?.SetActive(isActive);
    }

    private void OnTerminalSettingsChanged(object? sender, EventArgs e)
    {
        if (_terminalSettings is null)
        {
            return;
        }

        foreach (var session in Sessions)
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
