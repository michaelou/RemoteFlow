using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;
using SvcSystems.UI.Terminal;

namespace RemoteFlow.UI.ViewModels.Terminal;

public sealed partial class TerminalSessionViewModel : ObservableObject, IAsyncDisposable, IDisposable
{
    private readonly ITerminalChannel _channel;
    private readonly IUiDispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Utf8StreamDecoder _decoder = new();
    private readonly OscTitleParser _titleParser = new();
    private readonly Task _readTask;
    private readonly Task _exitTask;
    private int _disposeStarted;

    public TerminalSessionViewModel(
        ITerminalChannel channel,
        IUiDispatcher dispatcher,
        TerminalControlModel? model = null,
        string initialTitle = "Local shell",
        EnvironmentKind environment = EnvironmentKind.Unspecified,
        string? colorOverrideHex = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Model = model ?? new TerminalControlModel(new TerminalOptions
        {
            Cols = 120,
            Rows = 30,
            Scrollback = 10_000,
            ReflowOnResize = false,
            TermName = "xterm-256color",
        });
        Model.UserInput += OnUserInput;
        Title = string.IsNullOrWhiteSpace(initialTitle) ? "Terminal" : initialTitle.Trim();
        Environment = environment;
        AccentColorHex = ResolveAccentColor(environment, colorOverrideHex);
        State = SessionState.Connected;
        _readTask = ReadOutputAsync(_lifetime.Token);
        _exitTask = ObserveExitAsync();
        Completion = ObserveCompletionAsync();
    }

    public TerminalControlModel Model { get; }

    public Task Completion { get; }

    public int? ProcessId => (_channel as IPtySession)?.ProcessId;

    public EnvironmentKind Environment { get; }

    public string AccentColorHex { get; }

    public string TabBackgroundHex => IsActive ? $"#33{AccentColorHex[1..]}" : "#121821";

    public string ChromeTintHex => IsActive ? $"#1F{AccentColorHex[1..]}" : "#101418";

    public string EnvironmentCue => Environment switch
    {
        EnvironmentKind.Development => "DEV",
        EnvironmentKind.Staging => "STG",
        EnvironmentKind.Production => "PROD !",
        EnvironmentKind.Unspecified => "LOCAL",
        _ => throw new ArgumentOutOfRangeException(nameof(Environment)),
    };

    public bool IsProduction => Environment == EnvironmentKind.Production;

    public bool IsLive => State is SessionState.Created or SessionState.Connecting or
        SessionState.Connected or SessionState.Reconnecting;

    public bool IsEnded => !IsLive;

    public bool ApplicationCursorKeys { get; set; }

    [ObservableProperty]
    public partial string Title { get; private set; }

    [ObservableProperty]
    public partial string? UserTitleOverride { get; private set; }

    [ObservableProperty]
    public partial bool IsActive { get; internal set; }

    [ObservableProperty]
    public partial SessionState State { get; private set; } = SessionState.Created;

    [ObservableProperty]
    public partial string? EndedMessage { get; private set; }

    public void SetTitleOverride(string? title)
    {
        UserTitleOverride = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (UserTitleOverride is not null)
        {
            Title = UserTitleOverride;
        }
    }

    internal void SetActive(bool isActive)
    {
        IsActive = isActive;
    }

    public async ValueTask SendInputAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (data.IsEmpty || Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        try
        {
            await _channel.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetFailedAsync($"Terminal input failed: {exception.Message}").ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        Model.UserInput -= OnUserInput;
        _lifetime.Cancel();
        await _channel.DisposeAsync().ConfigureAwait(false);
        await Completion.ConfigureAwait(false);
        await _channel.Output.CompleteAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private async Task ReadOutputAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var result = await _channel.Output.ReadAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var text = _decoder.Decode(result.Buffer, flush: result.IsCompleted);
                    if (text.Length > 0)
                    {
                        var reportedTitles = _titleParser.Process(text);
                        await _dispatcher.InvokeAsync(() =>
                        {
                            Model.Feed(text);
                            if (UserTitleOverride is null && reportedTitles.Count > 0)
                            {
                                Title = reportedTitles[^1];
                            }
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _channel.Output.AdvanceTo(result.Buffer.End);
                }

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetFailedAsync($"Terminal output failed: {exception.Message}").ConfigureAwait(false);
        }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            var exitCode = await _channel.Exited.ConfigureAwait(false);
            void UpdateEndedState()
            {
                State = SessionState.Closed;
                EndedMessage = exitCode is { } code
                    ? $"Session ended (exit code {code})."
                    : "Session ended (terminated).";
            }

            if (Volatile.Read(ref _disposeStarted) != 0)
            {
                UpdateEndedState();
            }
            else
            {
                await _dispatcher.InvokeAsync(UpdateEndedState).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            await SetFailedAsync($"Terminal session failed: {exception.Message}").ConfigureAwait(false);
        }
    }

    private async Task ObserveCompletionAsync()
    {
        await Task.WhenAll(_readTask, _exitTask).ConfigureAwait(false);
    }

    private async void OnUserInput(object? sender, TerminalUserInputEventArgs e)
    {
        if (e.Data.IsEmpty || Volatile.Read(ref _disposeStarted) != 0)
        {
            return;
        }

        await SendInputAsync(e.Data, _lifetime.Token).ConfigureAwait(false);
    }

    private ValueTask SetFailedAsync(string message)
    {
        void UpdateFailedState()
        {
            State = SessionState.Failed;
            EndedMessage = message;
        }

        if (Volatile.Read(ref _disposeStarted) != 0)
        {
            UpdateFailedState();
            return ValueTask.CompletedTask;
        }

        return _dispatcher.InvokeAsync(UpdateFailedState);
    }

    partial void OnStateChanged(SessionState value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsEnded));
    }

    partial void OnIsActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(TabBackgroundHex));
        OnPropertyChanged(nameof(ChromeTintHex));
    }

    private static string ResolveAccentColor(EnvironmentKind environment, string? colorOverrideHex)
    {
        return !string.IsNullOrWhiteSpace(colorOverrideHex) &&
            System.Text.RegularExpressions.Regex.IsMatch(colorOverrideHex, "^#[0-9A-Fa-f]{6}$")
            ? colorOverrideHex.ToUpperInvariant()
            : environment switch
            {
                EnvironmentKind.Development => "#5DE28C",
                EnvironmentKind.Staging => "#FFCA58",
                EnvironmentKind.Production => "#FF7B72",
                EnvironmentKind.Unspecified => "#7E8998",
                _ => throw new ArgumentOutOfRangeException(nameof(environment)),
            };
    }
}
