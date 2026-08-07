using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Diagnostics;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IErrorDialogService errorDialogService) : IGlobalExceptionHandler, IDisposable
{
    private readonly ILogger<GlobalExceptionHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IErrorDialogService _errorDialogService =
        errorDialogService ?? throw new ArgumentNullException(nameof(errorDialogService));
    private int _installed;

    public void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) != 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public async Task HandleAsync(
        Exception exception,
        string context,
        bool isTerminating = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        if (isTerminating && _logger.IsEnabled(LogLevel.Critical))
        {
            _logger.LogCritical(exception, "Unhandled terminating exception in {ExceptionContext}", context);
        }
        else if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.LogError(exception, "Unhandled exception in {ExceptionContext}", context);
        }

        await _errorDialogService.ShowAsync(
            "RemoteFlow encountered an error",
            $"{context}: {exception.Message}",
            cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 0)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
    {
        var exception = eventArgs.ExceptionObject as Exception ?? new InvalidOperationException(
            $"A non-exception object was thrown: {eventArgs.ExceptionObject}");
        _ = HandleAsync(exception, "AppDomain", eventArgs.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        eventArgs.SetObserved();
        _ = HandleAsync(eventArgs.Exception, "background task");
    }
}
