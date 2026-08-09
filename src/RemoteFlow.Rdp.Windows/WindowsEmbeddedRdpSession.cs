using System.Globalization;
using System.Runtime.InteropServices;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Rdp.Windows.Interop;
using RemoteFlow.UI.Services;

namespace RemoteFlow.Rdp.Windows;

internal sealed class WindowsEmbeddedRdpSession : IEmbeddedRdpSession
{
    private readonly Lock _stateLock = new();
    private int _disposed;

    public WindowsEmbeddedRdpSession(INativeRdpControl control, IUiDispatcher dispatcher)
    {
        Control = control ?? throw new ArgumentNullException(nameof(control));
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Control.EventReceived += OnNativeEventReceived;
    }

    public event EventHandler<EmbeddedRdpSessionStateChangedEventArgs>? StateChanged;

    public EmbeddedRdpSessionState State
    {
        get
        {
            lock (_stateLock)
            {
                return CurrentState;
            }
        }
    }

    public string? StatusMessage
    {
        get
        {
            lock (_stateLock)
            {
                return CurrentStatusMessage;
            }
        }
    }

    internal INativeRdpControl NativeControl => Control;

    private INativeRdpControl Control { get; }

    private IUiDispatcher Dispatcher { get; }

    private EmbeddedRdpSessionState CurrentState { get; set; } = EmbeddedRdpSessionState.Created;

    private string? CurrentStatusMessage { get; set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            await TransitionAsync(EmbeddedRdpSessionState.Connecting, null, cancellationToken).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(
                () => Control.Connect(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FailWithoutThrowingAsync("The embedded RDP connection was cancelled.").ConfigureAwait(false);
        }
        catch (COMException exception)
        {
            await FailWithoutThrowingAsync($"The embedded RDP control could not connect: {exception.Message}").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await FailWithoutThrowingAsync($"The embedded RDP session could not connect: {exception.Message}").ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            await Dispatcher.InvokeAsync(
                () => Control.Disconnect(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FailWithoutThrowingAsync("Disconnecting the embedded RDP session was cancelled.").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await FailWithoutThrowingAsync($"The embedded RDP session could not disconnect: {exception.Message}").ConfigureAwait(false);
        }
    }

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            await TransitionAsync(EmbeddedRdpSessionState.Reconnecting, null, cancellationToken).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(
                () => Control.Connect(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FailWithoutThrowingAsync("Reconnecting the embedded RDP session was cancelled.").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await FailWithoutThrowingAsync($"The embedded RDP session could not reconnect: {exception.Message}").ConfigureAwait(false);
        }
    }

    public void Resize(int width, int height, double scaling)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scaling);
        // The native resize policy is added in #87. Until then, accepting a valid size is deliberately a no-op.
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Control.EventReceived -= OnNativeEventReceived;
        var disposalRan = false;
        try
        {
            await Dispatcher.InvokeAsync(() =>
                {
                    disposalRan = true;
                    Control.DisposeAsync().AsTask().GetAwaiter().GetResult();
                })
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            if (!disposalRan)
            {
                try
                {
                    await Control.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Teardown is best effort. A native cleanup failure must not escape application shutdown.
                }
            }
        }

        GC.SuppressFinalize(this);
    }

    private void OnNativeEventReceived(object? sender, NativeRdpEventArgs e)
    {
        _ = DispatchNativeEventAsync(e);
    }

    private async Task DispatchNativeEventAsync(NativeRdpEventArgs e)
    {
        try
        {
            await Dispatcher.InvokeAsync(() => HandleNativeEvent(e)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await FailWithoutThrowingAsync($"The embedded RDP control reported an error: {exception.Message}")
                .ConfigureAwait(false);
        }
    }

    private void HandleNativeEvent(NativeRdpEventArgs e)
    {
        switch (e.DispatchId)
        {
            case 1: // OnConnecting
            case 2: // OnConnected: transport is up, but the desktop is not usable until OnLoginComplete.
                break;
            case 3: // OnLoginComplete
            case 33: // OnAutoReconnected
                TransitionCore(EmbeddedRdpSessionState.Connected, null);
                break;
            case 4: // OnDisconnected
                HandleDisconnected(e.Arguments);
                break;
            case 10: // OnFatalError
                TransitionCore(
                    EmbeddedRdpSessionState.Failed,
                    $"The embedded RDP control reported a fatal error{FormatCode(e.Arguments)}.");
                break;
            case 22: // OnLogonError
                TransitionCore(
                    EmbeddedRdpSessionState.Failed,
                    "The RDP credentials were rejected. Check the username, password, and domain.");
                break;
            case 34: // OnAutoReconnecting2
                TransitionCore(EmbeddedRdpSessionState.Reconnecting, "The RDP connection was interrupted. Reconnecting…");
                break;
            default:
                // MSTSCLib has emitted undocumented dispids in production. Unknown events are benign.
                break;
        }
    }

    private void HandleDisconnected(IReadOnlyList<object?> arguments)
    {
        var disconnectReason = ReadUInt32(arguments, 0);
        var extendedReason = Control.ExtendedDisconnectReason;
        var message = RdpDisconnectReasonMessages.ToUserMessage(
            disconnectReason,
            extendedReason,
            Control.DescribeDisconnect(disconnectReason, extendedReason));
        TransitionCore(
            State is EmbeddedRdpSessionState.Connecting
                ? EmbeddedRdpSessionState.Failed
                : EmbeddedRdpSessionState.Disconnected,
            message);
    }

    private async ValueTask TransitionAsync(
        EmbeddedRdpSessionState nextState,
        string? message,
        CancellationToken cancellationToken)
    {
        await Dispatcher.InvokeAsync(() => TransitionCore(nextState, message), cancellationToken).ConfigureAwait(false);
    }

    private void TransitionCore(EmbeddedRdpSessionState nextState, string? message)
    {
        EmbeddedRdpSessionState previous;
        lock (_stateLock)
        {
            previous = CurrentState;
            if (previous == nextState)
            {
                CurrentStatusMessage = message ?? CurrentStatusMessage;
                return;
            }

            if (!IsLegal(previous, nextState))
            {
                return;
            }

            CurrentState = nextState;
            CurrentStatusMessage = message;
        }

        try
        {
            StateChanged?.Invoke(this, new(previous, nextState, message));
        }
        catch (Exception)
        {
            // A UI subscriber is part of the COM callback path. It must never unwind into mstscax.
        }
    }

    private async Task FailWithoutThrowingAsync(string message)
    {
        try
        {
            await Dispatcher.InvokeAsync(() => TransitionCore(EmbeddedRdpSessionState.Failed, message))
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            lock (_stateLock)
            {
                CurrentState = EmbeddedRdpSessionState.Failed;
                CurrentStatusMessage = message;
            }
        }
    }

    private static bool IsLegal(EmbeddedRdpSessionState current, EmbeddedRdpSessionState next)
    {
        return current switch
        {
            EmbeddedRdpSessionState.Created => next is EmbeddedRdpSessionState.Connecting or EmbeddedRdpSessionState.Failed,
            EmbeddedRdpSessionState.Connecting => next is EmbeddedRdpSessionState.Connected or EmbeddedRdpSessionState.Disconnected or EmbeddedRdpSessionState.Failed,
            EmbeddedRdpSessionState.Connected => next is EmbeddedRdpSessionState.Reconnecting or EmbeddedRdpSessionState.Disconnected or EmbeddedRdpSessionState.Failed,
            EmbeddedRdpSessionState.Reconnecting => next is EmbeddedRdpSessionState.Connected or EmbeddedRdpSessionState.Disconnected or EmbeddedRdpSessionState.Failed,
            EmbeddedRdpSessionState.Disconnected => next is EmbeddedRdpSessionState.Reconnecting or EmbeddedRdpSessionState.Failed,
            EmbeddedRdpSessionState.Failed => next is EmbeddedRdpSessionState.Connecting or EmbeddedRdpSessionState.Reconnecting,
            _ => false,
        };
    }

    private static uint ReadUInt32(IReadOnlyList<object?> arguments, int index)
    {
        return index < arguments.Count && arguments[index] is not null
            ? Convert.ToUInt32(arguments[index], CultureInfo.InvariantCulture)
            : 0u;
    }

    private static string FormatCode(IReadOnlyList<object?> arguments)
    {
        return arguments.Count == 0 || arguments[0] is null
            ? string.Empty
            : $" (code {Convert.ToString(arguments[0], CultureInfo.InvariantCulture)})";
    }
}
