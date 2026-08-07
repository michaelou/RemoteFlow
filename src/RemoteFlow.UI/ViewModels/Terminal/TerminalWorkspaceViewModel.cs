using System.Collections.ObjectModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Terminal;

public class TerminalWorkspaceViewModel : PageViewModel, IAsyncDisposable, IDisposable
{
    private readonly IPtyService? _ptyService;
    private readonly IUiDispatcher? _dispatcher;
    private readonly ISettingsStore? _settings;
    private readonly IConfirmationDialogService? _confirmation;
    private int _startingCount;
    private int _disposeStarted;

    public TerminalWorkspaceViewModel()
        : base("Terminals")
    {
    }

    public TerminalWorkspaceViewModel(IPtyService ptyService, IUiDispatcher dispatcher)
        : this(ptyService, dispatcher, null, null, null)
    {
    }

    public TerminalWorkspaceViewModel(
        IPtyService ptyService,
        IUiDispatcher dispatcher,
        ISettingsStore? settings,
        IConfirmationDialogService? confirmation,
        KeymapService? keymap)
        : base("Terminals")
    {
        _ptyService = ptyService ?? throw new ArgumentNullException(nameof(ptyService));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _settings = settings;
        _confirmation = confirmation;
        Keymap = keymap ?? new KeymapService();
    }

    public ObservableCollection<TerminalSessionViewModel> Sessions { get; } = [];

    public KeymapService Keymap { get; } = new();

    public TerminalSessionViewModel? SelectedSession { get; private set; }

    public TerminalSessionViewModel? Session => SelectedSession;

    public bool IsStarting => Volatile.Read(ref _startingCount) > 0;

    public string? ErrorMessage { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Sessions.Count == 0)
        {
            _ = await AddLocalSessionAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task<TerminalSessionViewModel?> AddLocalSessionAsync(
        CancellationToken cancellationToken = default)
    {
        return await AddSessionAsync(
            CreateDefaultShellOptions(),
            "Local shell",
            EnvironmentKind.Unspecified,
            colorOverrideHex: null,
            cancellationToken).ConfigureAwait(true);
    }

    public async Task<TerminalSessionViewModel?> AddSessionAsync(
        PtySpawnOptions options,
        string title,
        EnvironmentKind environment,
        string? colorOverrideHex,
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
            var channel = await _ptyService.SpawnAsync(options, cancellationToken).ConfigureAwait(true);
            var session = new TerminalSessionViewModel(
                channel,
                _dispatcher,
                initialTitle: title,
                environment: environment,
                colorOverrideHex: colorOverrideHex);
            Sessions.Add(session);
            SelectSession(session);
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetError($"The local shell could not be started: {exception.Message}");
            return null;
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

    private static void SetActive(TerminalSessionViewModel? session, bool isActive)
    {
        session?.SetActive(isActive);
    }
}
