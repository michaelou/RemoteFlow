using System.Runtime.InteropServices;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Rdp.Windows.Interop;
using RemoteFlow.UI.Services;
using Xunit;

namespace RemoteFlow.Rdp.Windows.Tests;

public sealed class WindowsEmbeddedRdpSessionTests
{
    [Fact]
    public async Task ConnectWaitsForLoginCompleteBeforeReportingConnected()
    {
        var control = new FakeNativeRdpControl();
        var dispatcher = new RecordingDispatcher();
        await using var session = new WindowsEmbeddedRdpSession(control, dispatcher);
        var transitions = new List<EmbeddedRdpSessionState>();
        session.StateChanged += (_, change) => transitions.Add(change.CurrentState);

        await session.ConnectAsync(TestContext.Current.CancellationToken);
        control.Raise(1);
        control.Raise(2);

        Assert.Equal(EmbeddedRdpSessionState.Connecting, session.State);
        Assert.Equal(1, control.ConnectCount);

        control.Raise(3);

        Assert.Equal(EmbeddedRdpSessionState.Connected, session.State);
        Assert.Equal(
            [EmbeddedRdpSessionState.Connecting, EmbeddedRdpSessionState.Connected],
            transitions);
        Assert.True(dispatcher.InvocationCount >= 4);
    }

    [Fact]
    public async Task AutoReconnectDropAndExplicitReconnectReuseTheSameControl()
    {
        var control = new FakeNativeRdpControl
        {
            ExtendedDisconnectReasonValue = 0,
            NativeDescription = "An internal error has occurred.",
        };
        await using var session = CreateSession(control);
        await ConnectThroughLoginAsync(session, control);

        control.Raise(34, 1, true, 1, 3);
        Assert.Equal(EmbeddedRdpSessionState.Reconnecting, session.State);
        control.Raise(33);
        Assert.Equal(EmbeddedRdpSessionState.Connected, session.State);

        control.Raise(4, 516u);
        Assert.Equal(EmbeddedRdpSessionState.Disconnected, session.State);
        Assert.Contains("could not be reached", session.StatusMessage, StringComparison.OrdinalIgnoreCase);

        await session.ReconnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(EmbeddedRdpSessionState.Reconnecting, session.State);
        Assert.Equal(2, control.ConnectCount);
        Assert.Same(control, session.NativeControl);
        control.Raise(3);
        Assert.Equal(EmbeddedRdpSessionState.Connected, session.State);
    }

    [Fact]
    public async Task ComFailureDuringConnectBecomesFailedState()
    {
        var control = new FakeNativeRdpControl
        {
            ConnectAction = _ => throw Marshal.GetExceptionForHR(unchecked((int)0x80004005))!,
        };
        await using var session = CreateSession(control);

        await session.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EmbeddedRdpSessionState.Failed, session.State);
        Assert.Contains("could not connect", session.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisconnectWhileConnectingBecomesFailure()
    {
        var control = new FakeNativeRdpControl();
        await using var session = CreateSession(control);
        await session.ConnectAsync(TestContext.Current.CancellationToken);

        control.Raise(4, 516u);

        Assert.Equal(EmbeddedRdpSessionState.Failed, session.State);
        Assert.Contains("could not be reached", session.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WrongPasswordBecomesSanitizedCredentialFailure()
    {
        const string secret = "NeverEchoThisPassword";
        var control = new FakeNativeRdpControl();
        await using var session = CreateSession(control);
        await session.ConnectAsync(TestContext.Current.CancellationToken);

        control.Raise(22, unchecked((int)0xC000006D));

        Assert.Equal(EmbeddedRdpSessionState.Failed, session.State);
        Assert.Contains("credentials were rejected", session.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, session.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DropMidSessionUsesReadableNativeReason()
    {
        var control = new FakeNativeRdpControl
        {
            ExtendedDisconnectReasonValue = 12,
            NativeDescription = "An internal error has occurred.",
        };
        await using var session = CreateSession(control);
        await ConnectThroughLoginAsync(session, control);

        control.Raise(4, 3u);

        Assert.Equal(EmbeddedRdpSessionState.Disconnected, session.State);
        Assert.Equal("An administrator ended the remote session.", session.StatusMessage);
    }

    [Fact]
    public async Task ExplicitDisconnectUsesNativeEventAndReadableState()
    {
        var control = new FakeNativeRdpControl();
        await using var session = CreateSession(control);
        await ConnectThroughLoginAsync(session, control);
        control.DisconnectAction = () => control.Raise(4, 1u);

        await session.DisconnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, control.DisconnectCount);
        Assert.Equal(EmbeddedRdpSessionState.Disconnected, session.State);
        Assert.Equal("The connection was closed.", session.StatusMessage);
    }

    [Fact]
    public async Task FatalErrorAndUnknownEventAreContained()
    {
        var control = new FakeNativeRdpControl();
        await using var session = CreateSession(control);
        await session.ConnectAsync(TestContext.Current.CancellationToken);

        control.Raise(27, "undocumented");
        Assert.Equal(EmbeddedRdpSessionState.Connecting, session.State);

        control.Raise(10, 42);
        Assert.Equal(EmbeddedRdpSessionState.Failed, session.State);
        Assert.Contains("fatal error", session.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThrowingStateSubscriberCannotEscapeNativeCallback()
    {
        var control = new FakeNativeRdpControl();
        await using var session = CreateSession(control);
        await session.ConnectAsync(TestContext.Current.CancellationToken);
        session.StateChanged += (_, _) => throw new InvalidOperationException("UI handler failed");

        var exception = Record.Exception(() => control.Raise(3));

        Assert.Null(exception);
        Assert.Equal(EmbeddedRdpSessionState.Connected, session.State);
    }

    [Fact]
    public async Task TwoSessionsKeepIndependentState()
    {
        var firstControl = new FakeNativeRdpControl();
        var secondControl = new FakeNativeRdpControl();
        await using var first = CreateSession(firstControl);
        await using var second = CreateSession(secondControl);
        await first.ConnectAsync(TestContext.Current.CancellationToken);
        await second.ConnectAsync(TestContext.Current.CancellationToken);

        firstControl.Raise(3);
        secondControl.Raise(22, -1);

        Assert.Equal(EmbeddedRdpSessionState.Connected, first.State);
        Assert.Equal(EmbeddedRdpSessionState.Failed, second.State);
    }

    [Fact]
    public async Task CancelledConnectRemainsDisposableAndReleasesExactlyOnce()
    {
        using var cancellation = new CancellationTokenSource();
        var control = new FakeNativeRdpControl
        {
            ConnectAction = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            },
        };
        var session = CreateSession(control);

        await session.ConnectAsync(cancellation.Token);
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(EmbeddedRdpSessionState.Failed, session.State);
        Assert.Equal(1, control.DisposeCount);
    }

    [Fact]
    public async Task DispatcherFailureDuringDisposeFallsBackWithoutLeaking()
    {
        var control = new FakeNativeRdpControl();
        var session = new WindowsEmbeddedRdpSession(control, new ThrowingDispatcher());

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, control.DisposeCount);
    }

    [Fact]
    public async Task ProviderReturnsSessionAndReportsActivationFailureAsResult()
    {
        var control = new FakeNativeRdpControl();
        var provider = new WindowsEmbeddedRdpSessionProvider(
            new FakeNativeRdpControlFactory(control),
            new RecordingDispatcher());
        var connection = CreateConnection();

        var success = await provider.CreateAsync(connection, TestContext.Current.CancellationToken);

        Assert.True(success.IsSuccess);
        _ = Assert.IsType<WindowsEmbeddedRdpSession>(success.Value);
        await success.Value.DisposeAsync();

        var unavailable = new WindowsEmbeddedRdpSessionProvider(
            new FakeNativeRdpControlFactory(new InvalidOperationException("COM unavailable")),
            new RecordingDispatcher());
        var failure = await unavailable.CreateAsync(connection, TestContext.Current.CancellationToken);

        Assert.True(failure.IsFailure);
        Assert.Equal(RemoteFlowErrorKind.Unavailable, failure.Error.Kind);
        Assert.Contains("could not be activated", failure.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(516u, 0u, "could not be reached")]
    [InlineData(0u, 5u, "session limit")]
    [InlineData(0u, 12u, "administrator")]
    [InlineData(0u, 256u, "licence")]
    public void KnownDisconnectReasonsHaveActionableWording(uint reason, uint extended, string expected)
    {
        var message = RdpDisconnectReasonMessages.ToUserMessage(
            reason,
            extended,
            "An internal error has occurred.");

        Assert.Contains(expected, message, StringComparison.OrdinalIgnoreCase);
    }

    private static WindowsEmbeddedRdpSession CreateSession(FakeNativeRdpControl control)
    {
        return new(control, new RecordingDispatcher());
    }

    private static async Task ConnectThroughLoginAsync(
        WindowsEmbeddedRdpSession session,
        FakeNativeRdpControl control)
    {
        await session.ConnectAsync(TestContext.Current.CancellationToken);
        control.Raise(3);
        Assert.Equal(EmbeddedRdpSessionState.Connected, session.State);
    }

    private static Connection CreateConnection()
    {
        return Connection.Create(
            SystemGuidProvider.Instance,
            "RDP server",
            "server.example.com",
            ProtocolType.Rdp).Value;
    }

    private sealed class RecordingDispatcher : IUiDispatcher
    {
        public int InvocationCount { get; private set; }

        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(action);
            InvocationCount++;
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingDispatcher : IUiDispatcher
    {
        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The dispatcher is shutting down.");
        }
    }

    private sealed class FakeNativeRdpControl : INativeRdpControl
    {
        public event EventHandler<NativeRdpEventArgs>? EventReceived;

        public object NativeInstance { get; } = new();

        public Action<CancellationToken>? ConnectAction { get; init; }

        public int ConnectCount { get; private set; }

        public int DisposeCount { get; private set; }

        public int DisconnectCount { get; private set; }

        public Action? DisconnectAction { get; set; }

        public uint ExtendedDisconnectReasonValue { get; init; }

        public uint ExtendedDisconnectReason => ExtendedDisconnectReasonValue;

        public string NativeDescription { get; init; } = "The connection was closed.";

        public void Connect(CancellationToken cancellationToken)
        {
            ConnectCount++;
            ConnectAction?.Invoke(cancellationToken);
        }

        public void Disconnect(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisconnectCount++;
            DisconnectAction?.Invoke();
        }

        public string DescribeDisconnect(uint disconnectReason, uint extendedDisconnectReason)
        {
            return NativeDescription;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }

        public void Raise(int dispatchId, params object?[] arguments)
        {
            EventReceived?.Invoke(this, new(dispatchId, arguments));
        }
    }

    private sealed class FakeNativeRdpControlFactory : INativeRdpControlFactory
    {
        private readonly INativeRdpControl? _control;
        private readonly Exception? _exception;

        public FakeNativeRdpControlFactory(INativeRdpControl control)
        {
            _control = control;
        }

        public FakeNativeRdpControlFactory(Exception exception)
        {
            _exception = exception;
        }

        public INativeRdpControl Create(RdpControlSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _exception is null ? _control! : throw _exception;
        }
    }
}
