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
    private readonly Task _readTask;
    private readonly Task _exitTask;
    private int _disposeStarted;

    public TerminalSessionViewModel(
        ITerminalChannel channel,
        IUiDispatcher dispatcher,
        TerminalControlModel? model = null)
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
        State = SessionState.Connected;
        _readTask = ReadOutputAsync(_lifetime.Token);
        _exitTask = ObserveExitAsync();
        Completion = ObserveCompletionAsync();
    }

    public TerminalControlModel Model { get; }

    public Task Completion { get; }

    [ObservableProperty]
    public partial SessionState State { get; private set; } = SessionState.Created;

    [ObservableProperty]
    public partial string? EndedMessage { get; private set; }

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
                        await _dispatcher.InvokeAsync(() => Model.Feed(text), cancellationToken).ConfigureAwait(false);
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

        try
        {
            await _channel.WriteAsync(e.Data, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await SetFailedAsync($"Terminal input failed: {exception.Message}").ConfigureAwait(false);
        }
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
}
