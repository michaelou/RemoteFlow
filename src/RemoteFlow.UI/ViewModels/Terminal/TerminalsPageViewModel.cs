using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Terminal;

namespace RemoteFlow.UI.ViewModels;

public sealed partial class TerminalsPageViewModel : PageViewModel, IAsyncDisposable, IDisposable
{
    private readonly IPtyService? _ptyService;
    private readonly IUiDispatcher? _dispatcher;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private int _disposeStarted;

    public TerminalsPageViewModel()
        : base("Terminals")
    {
    }

    public TerminalsPageViewModel(IPtyService ptyService, IUiDispatcher dispatcher)
        : base("Terminals")
    {
        _ptyService = ptyService;
        _dispatcher = dispatcher;
    }

    [ObservableProperty]
    public partial TerminalSessionViewModel? Session { get; private set; }

    [ObservableProperty]
    public partial bool IsStarting { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Session is null)
        {
            await StartLocalShellAsync(cancellationToken).ConfigureAwait(true);
        }
    }

    public async Task StartLocalShellAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        if (_ptyService is null || _dispatcher is null)
        {
            ErrorMessage = "Local terminal services are unavailable in preview mode.";
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            IsStarting = true;
            ErrorMessage = null;
            if (Session is not null)
            {
                await Session.DisposeAsync().ConfigureAwait(true);
                Session = null;
            }

            var channel = await _ptyService.SpawnAsync(CreateDefaultShellOptions(), cancellationToken).ConfigureAwait(true);
            Session = new TerminalSessionViewModel(channel, _dispatcher);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            ErrorMessage = $"The local shell could not be started: {exception.Message}";
        }
        finally
        {
            IsStarting = false;
            _ = _startGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        if (Session is not null)
        {
            await Session.DisposeAsync().ConfigureAwait(false);
            Session = null;
        }

        _startGate.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static PtySpawnOptions CreateDefaultShellOptions()
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
}
