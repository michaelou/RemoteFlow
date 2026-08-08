using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Platform;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

#pragma warning disable IDE0022 // Compact test platform stubs are easier to scan.

public sealed class ShellOpenServiceTests
{
    [Theory]
    [InlineData(OperatingSystemFamily.Windows, "explorer.exe")]
    [InlineData(OperatingSystemFamily.MacOs, "open")]
    [InlineData(OperatingSystemFamily.Linux, "xdg-open")]
    public async Task OpeningAFolderUsesTheNativeFileManagerOnEverySupportedOs(
        OperatingSystemFamily family,
        string executable)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var runner = new RecordingRunner();
            var service = new ShellOpenService(new StubPlatform(family), runner);

            var result = await service.OpenFolderAsync(directory, TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded);
            var request = Assert.Single(runner.Requests);
            Assert.Equal(executable, request.FileName);
            Assert.Equal(Path.GetFullPath(directory), Assert.Single(request.Arguments));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AFolderThatIsNotThereIsReportedRatherThanHandedToTheShell()
    {
        var runner = new RecordingRunner();
        var service = new ShellOpenService(new StubPlatform(OperatingSystemFamily.Linux), runner);
        var missing = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", Guid.NewGuid().ToString("N"));

        var result = await service.OpenFolderAsync(missing, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData(OperatingSystemFamily.MacOs, "open")]
    [InlineData(OperatingSystemFamily.Linux, "xdg-open")]
    public async Task OpeningALinkUsesTheNativeOpener(OperatingSystemFamily family, string executable)
    {
        var runner = new RecordingRunner();
        var service = new ShellOpenService(new StubPlatform(family), runner);

        var result = await service.OpenUrlAsync(
            new Uri("https://github.com/michaelou/RemoteFlow"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(executable, request.FileName);
        Assert.Equal("https://github.com/michaelou/RemoteFlow", Assert.Single(request.Arguments));
    }

    [Fact]
    public async Task OnWindowsTheUrlItselfIsShellExecutedSoTheDefaultBrowserHandlesIt()
    {
        var runner = new RecordingRunner();
        var service = new ShellOpenService(new StubPlatform(OperatingSystemFamily.Windows), runner);

        var result = await service.OpenUrlAsync(
            new Uri("https://github.com/michaelou/RemoteFlow"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var request = Assert.Single(runner.Requests);
        Assert.Equal("https://github.com/michaelou/RemoteFlow", request.FileName);
        Assert.True(request.UseShellExecute);
    }

    // The shell will launch whatever claims a scheme, so anything that is not a web page is refused
    // before it reaches the platform.
    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("javascript:alert(1)")]
    public async Task OnlyWebLinksAreOpened(string url)
    {
        var runner = new RecordingRunner();
        var service = new ShellOpenService(new StubPlatform(OperatingSystemFamily.Windows), runner);

        var result = await service.OpenUrlAsync(new Uri(url), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("Only http and https links can be opened.", result.ErrorMessage);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task AShellThatWillNotStartIsReportedRatherThanThrown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var service = new ShellOpenService(
                new StubPlatform(OperatingSystemFamily.Linux),
                new ThrowingRunner());

            var result = await service.OpenFolderAsync(directory, TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.Contains("could not be opened", result.ErrorMessage, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<ProcessLaunchRequest> Requests { get; } = [];

        public Task RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRunner : IProcessRunner
    {
        public Task RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("no file manager here");
    }

    private sealed class StubPlatform(OperatingSystemFamily family) : ISystemPlatform
    {
        public OperatingSystemFamily OperatingSystem => family;
        public string CurrentDirectory => Environment.CurrentDirectory;
        public string HomeDirectory => Environment.CurrentDirectory;
        public string? GetEnvironmentVariable(string name) => null;
        public string? FindExecutable(string name) => name;
        public bool FileExists(string path) => File.Exists(path);
        public string? GetLoginShellFromPasswd() => null;
    }
}

#pragma warning restore IDE0022
