using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Rdp.Windows.Interop;
using RemoteFlow.UI.Services;

namespace RemoteFlow.Rdp.Windows;

/// <summary>The Windows capability registration. Native session activation is implemented separately.</summary>
public sealed class WindowsEmbeddedRdpSessionProvider : IEmbeddedRdpSessionProvider
{
    private const int _initialViewportWidth = 1280;
    private const int _initialViewportHeight = 720;
    private readonly INativeRdpControlFactory _controlFactory;
    private readonly IUiDispatcher _dispatcher;

    public static WindowsEmbeddedRdpSessionProvider Instance { get; } = new(
        WindowsNativeRdpControlFactory.Instance,
        new UiDispatcher());

    internal WindowsEmbeddedRdpSessionProvider(
        INativeRdpControlFactory controlFactory,
        IUiDispatcher dispatcher)
    {
        _controlFactory = controlFactory ?? throw new ArgumentNullException(nameof(controlFactory));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool SupportsEmbeddedSessions => true;

    public Task<Result<IEmbeddedRdpSession>> CreateAsync(
        Connection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        cancellationToken.ThrowIfCancellationRequested();

        if (connection.Protocol != ProtocolType.Rdp)
        {
            return Task.FromResult(Result<IEmbeddedRdpSession>.Failure(RemoteFlowError.Validation(
                "embedded_rdp.not_an_rdp_connection",
                "The connection is not an RDP connection.")));
        }

        try
        {
            var settings = RdpControlSettingsMapper.Map(
                connection,
                _initialViewportWidth,
                _initialViewportHeight,
                displayScaling: 1d);
            var control = _controlFactory.Create(settings, cancellationToken);
            IEmbeddedRdpSession session = new WindowsEmbeddedRdpSession(control, _dispatcher);
            return Task.FromResult(Result<IEmbeddedRdpSession>.Success(session));
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Result<IEmbeddedRdpSession>.Failure(new RemoteFlowError(
                RemoteFlowErrorKind.Cancelled,
                "embedded_rdp.activation_cancelled",
                "Creating the embedded RDP session was cancelled.")));
        }
        catch (Exception exception)
        {
            return Task.FromResult(Result<IEmbeddedRdpSession>.Failure(RemoteFlowError.Unavailable(
                "embedded_rdp.activation_unavailable",
                $"The embedded RDP control could not be activated: {exception.Message}")));
        }
    }
}
