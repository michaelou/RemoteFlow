using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Desktop;
using RemoteFlow.Infrastructure.Diagnostics;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.Terminal;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Views;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class CompositionAndLoggingTests
{
    [AvaloniaFact]
    public void CompositionRootConstructsEveryRegisteredService()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        var builder = Host.CreateApplicationBuilder();
        _ = DesktopComposition.ConfigureServices(builder, paths);
        var descriptors = builder.Services.ToArray();
        using var host = builder.Build();

        foreach (var group in descriptors
                     .Where(descriptor => !descriptor.ServiceType.ContainsGenericParameters && !descriptor.IsKeyedService)
                     .GroupBy(descriptor => descriptor.ServiceType))
        {
            var resolved = host.Services.GetServices(group.Key).ToArray();
            Assert.Equal(group.Count(), resolved.Length);
            Assert.DoesNotContain(resolved, service => service is null);
        }

        Assert.NotNull(host.Services.GetRequiredService<MainWindow>());
        Assert.NotNull(host.Services.GetRequiredService<MainWindowViewModel>());
        var navigation = host.Services.GetRequiredService<INavigationService>();
        navigation.Navigate("terminals");
        Assert.Same(host.Services.GetRequiredService<TerminalsPageViewModel>(), navigation.CurrentPage);
    }

    [Fact]
    public void LoggerRedactsStructuredValuesMarkersPrivateKeysExceptionsAndSftpContents()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        var registry = new SecretRegistry();
        registry.Register("registered-marker-2468");
        var provider = new RedactingLoggerProvider(paths, registry);
        try
        {
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            var logger = factory.CreateLogger("RedactionTest");
            logger.LogError(
                new InvalidOperationException("exception contains registered-marker-2468"),
                "Password={Password}; Marker={Marker}; PrivateKey={PrivateKey}; Sftp={SftpFileContents}",
                "correct horse battery staple",
                "registered-marker-2468",
                "-----BEGIN OPENSSH PRIVATE KEY-----\nprivate-material\n-----END OPENSSH PRIVATE KEY-----",
                "confidential file contents");
        }
        finally
        {
            provider.Dispose();
        }

        var logFile = Assert.Single(Directory.GetFiles(paths.LogDirectory, "*.log"));
        var content = File.ReadAllText(logFile);
        Assert.Contains(RedactingLoggerProvider.RedactedValue, content, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse battery staple", content, StringComparison.Ordinal);
        Assert.DoesNotContain("registered-marker-2468", content, StringComparison.Ordinal);
        Assert.DoesNotContain("private-material", content, StringComparison.Ordinal);
        Assert.DoesNotContain("confidential file contents", content, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggerWritesUnderAppLogDirectoryAndRollsWithRetentionLimit()
    {
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        var provider = new RedactingLoggerProvider(paths, new SecretRegistry(), 1024, 7);
        try
        {
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            var logger = factory.CreateLogger("RollingTest");
            var padding = new string('x', 180);
            if (logger.IsEnabled(LogLevel.Information))
            {
                for (var index = 0; index < 200; index++)
                {
                    logger.LogInformation("Record {Index}: {Padding}", index, padding);
                }
            }
        }
        finally
        {
            provider.Dispose();
        }

        var files = Directory.GetFiles(paths.LogDirectory, "*.log");
        Assert.True(files.Length > 1);
        Assert.True(files.Length <= 7);
        Assert.All(files, file => Assert.Equal(paths.LogDirectory, Path.GetDirectoryName(file)));
    }

    [Fact]
    public async Task BackgroundExceptionIsLoggedAndPresented()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var paths = TestAppPaths.Under(directory.Path);
        var provider = new RedactingLoggerProvider(paths, new SecretRegistry());
        var dialog = new RecordingErrorDialogService();
        try
        {
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
            using var handler = new GlobalExceptionHandler(factory.CreateLogger<GlobalExceptionHandler>(), dialog);
            handler.Install();
            handler.Install();
            await handler.HandleAsync(
                new InvalidOperationException("background exploded"),
                "background task",
                cancellationToken: cancellationToken);
        }
        finally
        {
            provider.Dispose();
        }

        var logFile = Assert.Single(Directory.GetFiles(paths.LogDirectory, "*.log"));
        var content = await File.ReadAllTextAsync(logFile, cancellationToken);
        Assert.Contains("background task", content, StringComparison.Ordinal);
        Assert.Contains("background exploded", content, StringComparison.Ordinal);
        Assert.Equal("RemoteFlow encountered an error", dialog.Title);
        Assert.Contains("background task", dialog.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingErrorDialogService : IErrorDialogService
    {
        public string Title { get; private set; } = string.Empty;

        public string Message { get; private set; } = string.Empty;

        public Task ShowAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Title = title;
            Message = message;
            return Task.CompletedTask;
        }
    }
}
