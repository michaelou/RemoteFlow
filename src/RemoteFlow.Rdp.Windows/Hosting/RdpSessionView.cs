using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Views.Terminal;

namespace RemoteFlow.Rdp.Windows.Hosting;

/// <summary>An Avalonia view for one embedded RDP session. Parameterless construction never activates COM.</summary>
public sealed class RdpSessionView : UserControl, IAsyncDisposable
{
    private readonly RdpNativeHost _nativeHost;
    private readonly Border _recoveryPanel;
    private readonly TextBlock _statusText;
    private readonly TaskCompletionSource _initialViewportReady = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private OleRdpControlContainer? _container;
    private RdpKeyboardHook? _keyboardHook;
    private RdpViewportResizeController? _resizeController;
    private WindowsEmbeddedRdpSession? _windowsSession;
    private bool? _lastEffectiveVisibility;
    private double _lastRenderScaling;
    private int _disposed;

    public RdpSessionView()
    {
        AutomationProperties.SetHelpText(
            this,
            "Embedded remote desktop. F6 moves focus to its tab; Shift+F6 sends F6 to the remote computer.");
        _nativeHost = new RdpNativeHost
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _statusText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 560,
        };
        var reconnect = new Button
        {
            Content = "Reconnect",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        reconnect.Click += (_, _) => _ = ReconnectWithoutThrowingAsync();
        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 8,
            Children = { reconnect, close },
        };
        _recoveryPanel = new Border
        {
            IsVisible = false,
            Padding = new Thickness(24),
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Spacing = 16,
                Children = { _statusText, buttons },
            },
        };
        Content = new Grid
        {
            ClipToBounds = true,
            Children = { _nativeHost, _recoveryPanel },
        };
    }

    public RdpSessionView(IEmbeddedRdpSession session)
        : this()
    {
        AttachSession(session);
    }

    public event EventHandler? CloseRequested;

    public IEmbeddedRdpSession? Session { get; private set; }

    internal IntPtr ContainerWindowHandle => _nativeHost.ContainerWindowHandle;

    public void AttachSession(IEmbeddedRdpSession session)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(session);
        if (Session is not null)
        {
            throw new InvalidOperationException("This RDP view already has a session.");
        }
        if (session is not WindowsEmbeddedRdpSession windowsSession)
        {
            throw new ArgumentException("The Windows RDP view requires a Windows embedded session.", nameof(session));
        }

        var container = new OleRdpControlContainer(windowsSession.NativeControl);
        try
        {
            container.Create(1280, 720);
            _nativeHost.Attach(container);
            container.FocusChanged += OnNativeFocusChanged;
            _keyboardHook = new RdpKeyboardHook(
                container.Handle,
                () => _ = WorkspaceSessionContentHost.RequestFocusEscape(this));
            _container = container;
            Session = session;
            _windowsSession = windowsSession;
            _resizeController = new RdpViewportResizeController(windowsSession.ConfigureInitialViewport);
            Session.StateChanged += OnSessionStateChanged;
            SizeChanged += OnSizeChanged;
            LayoutUpdated += OnLayoutUpdated;
            UpdateState(Session.State, Session.StatusMessage);
            RequestCurrentViewport();
            SyncEffectiveVisibility();
        }
        catch
        {
            container.Dispose();
            throw;
        }
    }

    public bool FocusSurface()
    {
        return _container?.FocusControl() == true;
    }

    internal async Task WaitForInitialViewportAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _initialViewportReady.Task
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            // Keep the provider's safe 1280x720 at 100% if the view could not be realized promptly.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var session = Session;
        if (session is null)
        {
            DisposeContainer();
            GC.SuppressFinalize(this);
            return;
        }

        await RdpSessionTeardown.DisposeAsync(
            session,
            () => UnsubscribeEvents(session),
            DisposeContainer);
        GC.SuppressFinalize(this);
    }

    private void OnSessionStateChanged(object? sender, EmbeddedRdpSessionStateChangedEventArgs e)
    {
        UpdateState(e.CurrentState, e.StatusMessage);
    }

    private void OnNativeFocusChanged(object? sender, RdpControlFocusChangedEventArgs e)
    {
        PseudoClasses.Set(":native-focused", e.IsFocused);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        RequestCurrentViewport();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        SyncEffectiveVisibility();
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        if (Math.Abs(scaling - _lastRenderScaling) > double.Epsilon ||
            !_initialViewportReady.Task.IsCompleted)
        {
            _lastRenderScaling = scaling;
            RequestCurrentViewport();
        }
    }

    private void SyncEffectiveVisibility()
    {
        if (_lastEffectiveVisibility == IsEffectivelyVisible)
        {
            return;
        }

        _lastEffectiveVisibility = IsEffectivelyVisible;
        if (IsEffectivelyVisible)
        {
            // Capture the final tab/window geometry before releasing the one pending hidden resize.
            _resizeController?.SetVisible(false);
            RequestCurrentViewport(syncVisibility: false);
        }

        _resizeController?.SetVisible(IsEffectivelyVisible);
    }

    private void RequestCurrentViewport(bool syncVisibility = true)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1d;
        var width = (int)Math.Round(Bounds.Width * scaling, MidpointRounding.AwayFromZero);
        var height = (int)Math.Round(Bounds.Height * scaling, MidpointRounding.AwayFromZero);
        if (width > 0 && height > 0 && _windowsSession?.State == EmbeddedRdpSessionState.Created)
        {
            _windowsSession.ConfigureInitialViewport(width, height, scaling);
            _ = _initialViewportReady.TrySetResult();
            return;
        }

        if (syncVisibility)
        {
            _resizeController?.SetVisible(IsEffectivelyVisible);
        }
        _resizeController?.RequestResize(width, height, scaling);
    }

    private void UpdateState(EmbeddedRdpSessionState state, string? message)
    {
        var recoverable = state is EmbeddedRdpSessionState.Disconnected or EmbeddedRdpSessionState.Failed;
        _nativeHost.IsVisible = !recoverable;
        _recoveryPanel.IsVisible = recoverable;
        _statusText.Text = string.IsNullOrWhiteSpace(message)
            ? state == EmbeddedRdpSessionState.Disconnected
                ? "The remote desktop disconnected."
                : "The remote desktop could not connect."
            : message;
    }

    private async Task ReconnectWithoutThrowingAsync()
    {
        var session = Session;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.ReconnectAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The session reports expected failures through state and status text.
        }
    }

    private void DisposeContainer()
    {
        _keyboardHook?.Dispose();
        _keyboardHook = null;
        if (_container is { } container)
        {
            container.FocusChanged -= OnNativeFocusChanged;
        }
        _nativeHost.Release();
        _container?.Dispose();
        _container = null;
        _resizeController = null;
        _windowsSession = null;
        _lastEffectiveVisibility = null;
        _ = _initialViewportReady.TrySetCanceled();
        Session = null;
    }

    private void UnsubscribeEvents(IEmbeddedRdpSession session)
    {
        session.StateChanged -= OnSessionStateChanged;
        SizeChanged -= OnSizeChanged;
        LayoutUpdated -= OnLayoutUpdated;
        if (_container is { } container)
        {
            container.FocusChanged -= OnNativeFocusChanged;
        }
    }
}
