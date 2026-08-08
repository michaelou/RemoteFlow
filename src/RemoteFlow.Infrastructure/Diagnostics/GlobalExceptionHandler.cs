using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Diagnostics;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IErrorDialogService errorDialogService,
    ILastErrorStore? lastErrorStore = null,
    IClock? clock = null) : IGlobalExceptionHandler, IDisposable
{
    private readonly ILogger<GlobalExceptionHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IErrorDialogService _errorDialogService =
        errorDialogService ?? throw new ArgumentNullException(nameof(errorDialogService));
    // Optional: an error handler that cannot be constructed because a diagnostic aid is missing would be
    // the worst possible failure to introduce here.
    private readonly ILastErrorStore? _lastErrorStore = lastErrorStore;
    private readonly IClock _clock = clock ?? SystemClock.Instance;
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

        // Recorded before the log write, so the about box can still say what happened when the failure is
        // the logger itself.
        _lastErrorStore?.Record(exception, context, _clock.UtcNow);

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
