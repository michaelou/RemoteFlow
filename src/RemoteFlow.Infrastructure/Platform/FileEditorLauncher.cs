using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Infrastructure.Platform;

public sealed class FileEditorLauncher(
    ISystemPlatform platform,
    IProcessRunner processRunner) : IFileEditorLauncher
{
    public async Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The local editing copy was not found.", fullPath);
        }

        var request = platform.OperatingSystem switch
        {
            OperatingSystemFamily.Windows => new ProcessLaunchRequest(
                fullPath,
                [],
                Path.GetDirectoryName(fullPath),
                UseShellExecute: true),
            OperatingSystemFamily.MacOs => new ProcessLaunchRequest(
                platform.FindExecutable("open") ?? "/usr/bin/open",
                [fullPath],
                Path.GetDirectoryName(fullPath)),
            OperatingSystemFamily.Linux => new ProcessLaunchRequest(
                platform.FindExecutable("xdg-open") ?? "xdg-open",
                [fullPath],
                Path.GetDirectoryName(fullPath)),
            _ => throw new PlatformNotSupportedException("No default editor launcher is available."),
        };
        await processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
