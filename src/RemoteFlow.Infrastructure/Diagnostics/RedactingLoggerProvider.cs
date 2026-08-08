using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Abstractions;
using Serilog;
using Serilog.Events;

namespace RemoteFlow.Infrastructure.Diagnostics;

public sealed partial class RedactingLoggerProvider : ILoggerProvider
{
    public const long FileSizeLimitBytes = 10 * 1024 * 1024;
    public const int RetainedFileCountLimit = 7;
    public const string RedactedValue = "[REDACTED]";

    private static readonly string[] _sensitiveNameFragments =
    [
        "credential",
        "password",
        "passphrase",
        "privatekey",
        "private_key",
        "secret",
        "token",
        "sftpfilecontent",
        "sftpcontent",
        "filecontents",
        "agentresponse",
        "authenticationresponse",
    ];

    private readonly Serilog.ILogger _logger;
    private readonly ISecretRegistry _secretRegistry;

    public RedactingLoggerProvider(IAppPaths appPaths, ISecretRegistry secretRegistry)
        : this(appPaths, secretRegistry, FileSizeLimitBytes, RetainedFileCountLimit)
    {
    }

    public RedactingLoggerProvider(
        IAppPaths appPaths,
        ISecretRegistry secretRegistry,
        long fileSizeLimitBytes,
        int retainedFileCountLimit)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _secretRegistry = secretRegistry ?? throw new ArgumentNullException(nameof(secretRegistry));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fileSizeLimitBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retainedFileCountLimit);
        appPaths.EnsureDirectories();
        var logPath = Path.Combine(appPaths.LogDirectory, "remoteflow-.log");
        _logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFileCountLimit,
                shared: true,
                formatProvider: CultureInfo.InvariantCulture)
            .CreateLogger();
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return new RedactingLogger(categoryName, _logger, _secretRegistry);
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
    }

    private sealed class RedactingLogger(
        string categoryName,
        Serilog.ILogger logger,
        ISecretRegistry secretRegistry) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel != LogLevel.None;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message}{Environment.NewLine}{exception}";
            }
            foreach (var value in SensitiveValues(state))
            {
                message = message.Replace(value, RedactedValue, StringComparison.Ordinal);
            }

            foreach (var marker in secretRegistry.GetSecrets())
            {
                message = message.Replace(marker, RedactedValue, StringComparison.Ordinal);
            }

            message = PrivateKeyPattern().Replace(message, RedactedValue);
            message = NamedSecretPattern().Replace(message, match => $"{match.Groups[1].Value}{RedactedValue}");
            logger.Write(ToSerilogLevel(logLevel), "{Category}: {Message}", categoryName, message);
        }

        private static IEnumerable<string> SensitiveValues<TState>(TState state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
            {
                yield break;
            }

            foreach (var property in properties)
            {
                if (property.Value is not null && IsSensitiveName(property.Key))
                {
                    var value = Convert.ToString(property.Value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(value))
                    {
                        yield return value;
                    }
                }
            }
        }

        private static bool IsSensitiveName(string name)
        {
            var normalized = name.Replace("-", string.Empty, StringComparison.Ordinal);
            return _sensitiveNameFragments.Any(fragment => normalized.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private static LogEventLevel ToSerilogLevel(LogLevel level)
    {
        return level switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            LogLevel.None => LogEventLevel.Information,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unsupported log level."),
        };
    }

    [GeneratedRegex("-----BEGIN [^-\\r\\n]*PRIVATE KEY-----[\\s\\S]*?-----END [^-\\r\\n]*PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyPattern();

    [GeneratedRegex("(?i)\\b(password|passphrase|credential|private[_-]?key|secret|token)\\s*[:=]\\s*([^\\s,;]+)")]
    private static partial Regex NamedSecretPattern();
}
