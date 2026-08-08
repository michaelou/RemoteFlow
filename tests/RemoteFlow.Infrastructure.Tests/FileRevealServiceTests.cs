using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Platform;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

#pragma warning disable IDE0022 // Compact test platform stubs are easier to scan.

public sealed class FileRevealServiceTests
{
    [Theory]
    [InlineData(OperatingSystemFamily.Windows, "explorer.exe")]
    [InlineData(OperatingSystemFamily.MacOs, "open")]
    [InlineData(OperatingSystemFamily.Linux, "xdg-open")]
    public async Task RevealUsesNativeFileManagerOnEverySupportedOs(
        OperatingSystemFamily family,
        string executable)
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "finished.txt");
        await File.WriteAllTextAsync(path, "done", TestContext.Current.CancellationToken);
        try
        {
            var runner = new RecordingRunner();
            var service = new FileRevealService(new StubPlatform(family), runner);

            var result = await service.RevealAsync(path, TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded);
            var request = Assert.Single(runner.Requests);
            Assert.Equal(executable, request.FileName);
            if (family == OperatingSystemFamily.Windows)
            {
                Assert.Equal($"/select,{Path.GetFullPath(path)}", Assert.Single(request.Arguments));
            }
            else if (family == OperatingSystemFamily.MacOs)
            {
                Assert.Equal(["-R", Path.GetFullPath(path)], request.Arguments);
            }
            else
            {
                Assert.Equal(Path.GetFullPath(directory), Assert.Single(request.Arguments));
            }
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
