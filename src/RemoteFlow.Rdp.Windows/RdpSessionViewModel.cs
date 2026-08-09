using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Rdp.Windows.Hosting;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Terminal;

namespace RemoteFlow.Rdp.Windows;

/// <summary>Adapts one embedded RDP session to the protocol-neutral workspace contract.</summary>
public sealed class RdpSessionViewModel : ObservableObject,
    IWorkspaceSessionViewModel,
    IWorkspaceSessionContentProvider,
    IWorkspaceSessionFocusTarget,
    IWorkspaceSessionCloseRequestSource,
    IEmbeddedRdpWorkspaceSession
{
    private readonly IEmbeddedRdpSession _session;
    private readonly AsyncRelayCommand _retryCommand;
    private RdpSessionView? _view;
    private int _disposed;

    public RdpSessionViewModel(
        IEmbeddedRdpSession session,
        string title,
        EnvironmentKind environment = EnvironmentKind.Unspecified,
        string? colorOverrideHex = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Title = string.IsNullOrWhiteSpace(title) ? "Remote desktop" : title.Trim();
        Environment = environment;
        AccentColorHex = WorkspaceSessionAppearance.ResolveAccentColor(environment, colorOverrideHex);
        _retryCommand = new AsyncRelayCommand(RetryAsync, CanRetry);
        _session.StateChanged += OnSessionStateChanged;
    }

    public event EventHandler? CloseRequested;

    public string Title { get; }

    public string TabTitle => Title;

    public EnvironmentKind Environment { get; }

    public string AccentColorHex { get; }

    public string TabBackgroundHex => IsActive ? $"#33{AccentColorHex[1..]}" : "#121821";

    public string ChromeTintHex => IsActive ? $"#1F{AccentColorHex[1..]}" : "#101418";

    public string EnvironmentCue => WorkspaceSessionAppearance.EnvironmentCue(Environment);

    public string ProtocolCue => "RDP";

    public string StatusText => _session.State switch
    {
        EmbeddedRdpSessionState.Created => "Created",
        EmbeddedRdpSessionState.Connecting => "Connecting",
        EmbeddedRdpSessionState.Connected => "Connected",
        EmbeddedRdpSessionState.Reconnecting => "Reconnecting",
        EmbeddedRdpSessionState.Disconnected => "Disconnected",
        EmbeddedRdpSessionState.Failed => "Failed",
        _ => throw new ArgumentOutOfRangeException(nameof(_session.State)),
    };

    public string TabAccessibleName =>
        $"{TabTitle}, RDP, {WorkspaceSessionAppearance.EnvironmentDescription(Environment)}, {StatusText}";

    public string CloseTabAccessibleName => $"Close RDP session {TabTitle}";

    public bool IsActive { get; private set; }

    public bool IsLive => _session.State is EmbeddedRdpSessionState.Created or
        EmbeddedRdpSessionState.Connecting or EmbeddedRdpSessionState.Connected or
        EmbeddedRdpSessionState.Reconnecting;

    public bool IsEnded => !IsLive;

    public bool CanOpenInSystemTerminal => false;

    public string? EndedMessage => IsEnded ? _session.StatusMessage : null;

    public string RecoveryActionLabel => _session.State == EmbeddedRdpSessionState.Disconnected
        ? "Reconnect"
        : "Retry";

    public IAsyncRelayCommand RetryCommand => _retryCommand;

    public void PrepareForConnect()
    {
        _ = CreateSessionContent();
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        return _session.ConnectAsync(cancellationToken);
    }

    public void SetActive(bool isActive)
    {
        if (IsActive == isActive)
        {
            return;
        }

        IsActive = isActive;
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(TabBackgroundHex));
        OnPropertyChanged(nameof(ChromeTintHex));
    }

    public Control CreateSessionContent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_view is not null)
        {
            return _view;
        }

        _view = new RdpSessionView(_session);
        _view.CloseRequested += OnViewCloseRequested;
        return _view;
    }

    public bool FocusSessionContent()
    {
        return _view?.FocusSurface() == true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _session.StateChanged -= OnSessionStateChanged;
        if (_view is not null)
        {
            _view.CloseRequested -= OnViewCloseRequested;
            await _view.DisposeAsync().ConfigureAwait(false);
            _view = null;
        }
        else
        {
            if (_session.State is not EmbeddedRdpSessionState.Created and
                not EmbeddedRdpSessionState.Disconnected and
                not EmbeddedRdpSessionState.Failed)
            {
                await _session.DisconnectAsync().ConfigureAwait(false);
            }
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private bool CanRetry()
    {
        return _session.State is EmbeddedRdpSessionState.Disconnected or EmbeddedRdpSessionState.Failed;
    }

    private async Task RetryAsync(CancellationToken cancellationToken)
    {
        await _session.ReconnectAsync(cancellationToken).ConfigureAwait(true);
    }

    private void OnSessionStateChanged(object? sender, EmbeddedRdpSessionStateChangedEventArgs e)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(TabAccessibleName));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsEnded));
        OnPropertyChanged(nameof(EndedMessage));
        OnPropertyChanged(nameof(RecoveryActionLabel));
        _retryCommand.NotifyCanExecuteChanged();
    }

    private void OnViewCloseRequested(object? sender, EventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
