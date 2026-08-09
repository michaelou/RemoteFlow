using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
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
            new RecordingDispatcher(),
            []);
        var connection = CreateConnection();

        var success = await provider.CreateAsync(connection, TestContext.Current.CancellationToken);

        Assert.True(success.IsSuccess);
        _ = Assert.IsType<WindowsEmbeddedRdpSession>(success.Value);
        await success.Value.DisposeAsync();

        var unavailable = new WindowsEmbeddedRdpSessionProvider(
            new FakeNativeRdpControlFactory(new InvalidOperationException("COM unavailable")),
            new RecordingDispatcher(),
            []);
        var failure = await unavailable.CreateAsync(connection, TestContext.Current.CancellationToken);

        Assert.True(failure.IsFailure);
        Assert.Equal(RemoteFlowErrorKind.Unavailable, failure.Error.Kind);
        Assert.Contains("could not be activated", failure.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClipboardRedirectionIsMappedPerNativeControlBeforeEitherSessionConnects()
    {
        var enabledControl = new FakeNativeRdpControl();
        var disabledControl = new FakeNativeRdpControl();
        var factory = new FakeNativeRdpControlFactory(enabledControl, disabledControl);
        var provider = new WindowsEmbeddedRdpSessionProvider(factory, new RecordingDispatcher(), []);

        var enabled = await provider.CreateAsync(
            CreateConnectionWithClipboardRedirection(enabled: true),
            TestContext.Current.CancellationToken);
        var disabled = await provider.CreateAsync(
            CreateConnectionWithClipboardRedirection(enabled: false),
            TestContext.Current.CancellationToken);

        Assert.True(enabled.IsSuccess);
        Assert.True(disabled.IsSuccess);
        Assert.True(enabledControl.RedirectClipboard);
        Assert.False(disabledControl.RedirectClipboard);
        Assert.Equal([true, false], factory.CreatedSettings.Select(settings =>
            settings.AdvancedSettings.RedirectClipboard));
        await enabled.Value.DisposeAsync();
        await disabled.Value.DisposeAsync();
    }

    [Fact]
    public async Task StoredPasswordIsAppliedOnceWithSavingDisabledAndNoExternalLauncherDependency()
    {
        const string secret = "Correct-Horse-Battery-Staple";
        var connection = CreateConnectionWithCredential();
        var provider = new MutableCredentialProvider(secret);
        var control = new FakeNativeRdpControl();
        await using var session = new WindowsEmbeddedRdpSession(
            control,
            new RecordingDispatcher(),
            new EmbeddedRdpCredentialSource(connection, [provider]));

        await session.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(secret, control.ClearTextPassword);
        Assert.Equal(1, control.SetPasswordCount);
        Assert.False(control.AllowCredentialSaving);
        Assert.True(control.AllowPromptingForCredentials);
        Assert.True(provider.IssuedHandles.Single().IsDisposed);
        Assert.DoesNotContain(
            typeof(WindowsEmbeddedRdpSessionProvider).Assembly.GetReferencedAssemblies(),
            assembly => assembly.Name == "RemoteFlow.Infrastructure");
        Assert.DoesNotContain(
            typeof(WindowsEmbeddedRdpSessionProvider).GetConstructors(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType.Name.Contains("Process", StringComparison.OrdinalIgnoreCase) ||
                parameter.ParameterType.Name.Contains("Path", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeletedCredentialIsResetAndReconnectPromptsInsteadOfReusingIt()
    {
        var connection = CreateConnectionWithCredential();
        var provider = new MutableCredentialProvider("first-secret");
        var control = new FakeNativeRdpControl();
        await using var session = new WindowsEmbeddedRdpSession(
            control,
            new RecordingDispatcher(),
            new EmbeddedRdpCredentialSource(connection, [provider]));
        await ConnectThroughLoginAsync(session, control);
        Assert.Equal("first-secret", control.ClearTextPassword);

        provider.Secret = null;
        control.Raise(4, 1u);
        await session.ReconnectAsync(TestContext.Current.CancellationToken);

        Assert.Null(control.ClearTextPassword);
        Assert.Equal(2, control.ResetPasswordCount);
        Assert.Equal(1, control.SetPasswordCount);
        Assert.True(control.AllowPromptingForCredentials);
        Assert.False(control.AllowCredentialSaving);
    }

    [Fact]
    public async Task TraceLoggingAndFailuresNeverContainPassword()
    {
        const string secret = "Do-Not-Log-This-Secret";
        var connection = CreateConnectionWithCredential();
        var provider = new MutableCredentialProvider(secret);
        var logger = new TraceLogger();

        var successfulControl = new FakeNativeRdpControl();
        await using (var successful = new WindowsEmbeddedRdpSession(
            successfulControl,
            new RecordingDispatcher(),
            new EmbeddedRdpCredentialSource(connection, [provider]),
            logger))
        {
            await successful.ConnectAsync(TestContext.Current.CancellationToken);
            successfulControl.Raise(3);
            Assert.Equal(EmbeddedRdpSessionState.Connected, successful.State);
        }

        var failedControl = new FakeNativeRdpControl
        {
            ConnectAction = _ => throw new InvalidOperationException($"native failure contained {secret}"),
        };
        await using var failed = new WindowsEmbeddedRdpSession(
            failedControl,
            new RecordingDispatcher(),
            new EmbeddedRdpCredentialSource(connection, [provider]),
            logger);

        var exception = await Record.ExceptionAsync(() =>
            failed.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Null(exception);
        Assert.Equal(EmbeddedRdpSessionState.Failed, failed.State);
        Assert.DoesNotContain(secret, failed.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Messages, message => message.Contains(secret, StringComparison.Ordinal));
        Assert.All(provider.IssuedHandles, handle => Assert.True(handle.IsDisposed));
    }

    [Fact]
    public async Task AuthenticationWarningIsSurfacedWithoutWeakeningSecuritySettings()
    {
        var control = new FakeNativeRdpControl();
        await using var session = CreateSession(control);
        string? warning = null;
        session.StateChanged += (_, change) => warning = change.StatusMessage;
        await session.ConnectAsync(TestContext.Current.CancellationToken);

        control.Raise(18);

        Assert.Equal(EmbeddedRdpSessionState.Connecting, session.State);
        Assert.Contains("certificate or identity warning", warning, StringComparison.OrdinalIgnoreCase);
        Assert.False(control.AllowCredentialSaving);
        Assert.True(control.AllowPromptingForCredentials);
    }

    [Fact]
    public async Task ResizeBurstAppliesOnlyLatestPhysicalViewportWithoutReconnect()
    {
        var time = new FakeTimeProvider();
        var control = new FakeNativeRdpControl();
        await using var session = new WindowsEmbeddedRdpSession(
            control,
            new RecordingDispatcher(),
            timeProvider: time);
        await ConnectThroughLoginAsync(session, control);

        session.Resize(1000, 700, 1d);
        session.Resize(1100, 750, 1.25d);
        session.Resize(1200, 800, 1.5d);
        time.Advance(WindowsEmbeddedRdpSession.ResizeDebounce);
        await control.ResizeObserved.Task.WaitAsync(TestContext.Current.CancellationToken);

        var resize = Assert.Single(control.ResizeRequests);
        Assert.Equal((1200, 800, 140u, 140u), resize);
        Assert.Equal(1, control.ConnectCount);
        Assert.Empty(control.SmartSizingValues);
        Assert.Equal(EmbeddedRdpSessionState.Connected, session.State);
    }

    [Fact]
    public async Task InitialViewportSendsMapperScaleFactorsToFakeControlBeforeConnect()
    {
        var control = new FakeNativeRdpControl();
        await using var session = CreateSession(control);

        session.ConfigureInitialViewport(2400, 1350, 2d);

        Assert.Equal([(2400, 1350, 180u, 180u)], control.InitialDisplayRequests);
        Assert.Equal(0, control.ConnectCount);
        await session.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, control.ConnectCount);
    }

    [Fact]
    public async Task UnsupportedInitialDpiUsesSmartSizingWithoutBlockingConnect()
    {
        var logger = new TraceLogger();
        var control = new FakeNativeRdpControl
        {
            InitialDisplayResult = NativeRdpResizeResult.Failure("IMsRdpExtendedSettings is unavailable"),
        };
        await using var session = new WindowsEmbeddedRdpSession(
            control,
            new RecordingDispatcher(),
            logger: logger);

        session.ConfigureInitialViewport(1800, 1200, 1.5d);
        await session.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal([true], control.SmartSizingValues);
        Assert.Equal(1, control.ConnectCount);
        Assert.Contains(logger.Messages, message =>
            message.Contains("Initial RDP display scaling", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResizeIsNoOpUntilConnectedAndAfterDisconnect()
    {
        var time = new FakeTimeProvider();
        var control = new FakeNativeRdpControl();
        await using var session = new WindowsEmbeddedRdpSession(
            control,
            new RecordingDispatcher(),
            timeProvider: time);

        session.Resize(0, 0, 0);
        await session.ConnectAsync(TestContext.Current.CancellationToken);
        session.Resize(0, 0, 0);
        control.Raise(4, 1u);
        session.Resize(0, 0, 0);
        time.Advance(WindowsEmbeddedRdpSession.ResizeDebounce);
        await Task.Yield();

        Assert.Empty(control.ResizeRequests);
    }

    [Fact]
    public async Task FailedDynamicResizeEnablesSmartSizingOnceAndKeepsSessionConnected()
    {
        var time = new FakeTimeProvider();
        var logger = new TraceLogger();
        var control = new FakeNativeRdpControl
        {
            ResizeResult = NativeRdpResizeResult.Failure("COMException (HRESULT 0x80004001)"),
        };
        await using var session = new WindowsEmbeddedRdpSession(
            control,
            new RecordingDispatcher(),
            logger: logger,
            timeProvider: time);
        await ConnectThroughLoginAsync(session, control);

        session.Resize(1440, 900, 1d);
        time.Advance(WindowsEmbeddedRdpSession.ResizeDebounce);
        await control.ResizeObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        session.Resize(1600, 1000, 1d);
        time.Advance(WindowsEmbeddedRdpSession.ResizeDebounce);
        await Task.Yield();

        _ = Assert.Single(control.ResizeRequests);
        Assert.Equal([true], control.SmartSizingValues);
        Assert.Equal(EmbeddedRdpSessionState.Connected, session.State);
        Assert.Equal(1, control.ConnectCount);
        Assert.Contains(logger.Messages, message =>
            message.Contains("SmartSizing fallback was enabled", StringComparison.Ordinal));
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

    private static Connection CreateConnectionWithCredential()
    {
        var connection = CreateConnection();
        var credential = CredentialRef.Create(
            CredentialKind.RdpPassword,
            "rdp/server.example.com",
            "test-provider").Value;
        return connection.SetCredential(credential, SystemGuidProvider.Instance);
    }

    private static Connection CreateConnectionWithClipboardRedirection(bool enabled)
    {
        var connection = CreateConnection();
        var options = RdpOptions.Default();
        _ = options.Configure(redirectClipboard: enabled);
        return connection.SetOptions(
            SshOptions.Default(),
            SftpOptions.Default(),
            options,
            SystemGuidProvider.Instance);
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

        public bool AllowCredentialSaving { get; private set; }

        public bool AllowPromptingForCredentials { get; private set; }

        public string? ClearTextPassword { get; private set; }

        public int ResetPasswordCount { get; private set; }

        public int SetPasswordCount { get; private set; }

        public bool RedirectClipboard { get; set; }

        public NativeRdpResizeResult ResizeResult { get; init; } = NativeRdpResizeResult.Success;

        public NativeRdpResizeResult SmartSizingResult { get; init; } = NativeRdpResizeResult.Success;

        public List<(int Width, int Height, uint DesktopScale, uint DeviceScale)> ResizeRequests { get; } = [];

        public List<bool> SmartSizingValues { get; } = [];

        public TaskCompletionSource ResizeObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NativeRdpResizeResult InitialDisplayResult { get; init; } = NativeRdpResizeResult.Success;

        public List<(int Width, int Height, uint DesktopScale, uint DeviceScale)> InitialDisplayRequests { get; } = [];

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

        public void ConfigureCredentialPolicy(bool allowCredentialSaving, bool allowPromptingForCredentials)
        {
            AllowCredentialSaving = allowCredentialSaving;
            AllowPromptingForCredentials = allowPromptingForCredentials;
        }

        public void SetClearTextPassword(ReadOnlySpan<char> password)
        {
            SetPasswordCount++;
            ClearTextPassword = new string(password);
        }

        public void ResetPassword()
        {
            ResetPasswordCount++;
            ClearTextPassword = null;
        }

        public NativeRdpResizeResult ConfigureInitialDisplaySettings(
            int width,
            int height,
            uint desktopScaleFactor,
            uint deviceScaleFactor)
        {
            InitialDisplayRequests.Add((width, height, desktopScaleFactor, deviceScaleFactor));
            return InitialDisplayResult;
        }

        public NativeRdpResizeResult UpdateSessionDisplaySettings(
            int width,
            int height,
            uint desktopScaleFactor,
            uint deviceScaleFactor)
        {
            ResizeRequests.Add((width, height, desktopScaleFactor, deviceScaleFactor));
            _ = ResizeObserved.TrySetResult();
            return ResizeResult;
        }

        public NativeRdpResizeResult SetSmartSizing(bool enabled)
        {
            SmartSizingValues.Add(enabled);
            return SmartSizingResult;
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

    private sealed class MutableCredentialProvider(string? secret) : ICredentialProvider
    {
        public string Name => "test-provider";

        public bool IsAvailable => true;

        public string? Secret { get; set; } = secret;

        public List<SecretHandle> IssuedHandles { get; } = [];

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Secret is null)
            {
                return Task.FromResult<SecretHandle?>(null);
            }

            var handle = new SecretHandle(Secret);
            IssuedHandles.Add(handle);
            return Task.FromResult<SecretHandle?>(handle);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> value,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            Secret = null;
            return Task.CompletedTask;
        }
    }

    private sealed class TraceLogger : ILogger<WindowsEmbeddedRdpSession>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Trace;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class FakeNativeRdpControlFactory : INativeRdpControlFactory
    {
        private readonly Queue<INativeRdpControl> _controls = [];
        private readonly Exception? _exception;

        public FakeNativeRdpControlFactory(INativeRdpControl control)
        {
            _controls.Enqueue(control);
        }

        public FakeNativeRdpControlFactory(params INativeRdpControl[] controls)
        {
            foreach (var control in controls)
            {
                _controls.Enqueue(control);
            }
        }

        public FakeNativeRdpControlFactory(Exception exception)
        {
            _exception = exception;
        }

        public List<RdpControlSettings> CreatedSettings { get; } = [];

        public INativeRdpControl Create(RdpControlSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_exception is not null)
            {
                throw _exception;
            }

            CreatedSettings.Add(settings);
            var control = _controls.Dequeue();
            if (control is FakeNativeRdpControl fake)
            {
                fake.RedirectClipboard = settings.AdvancedSettings.RedirectClipboard;
            }
            return control;
        }
    }
}
