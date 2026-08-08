using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Platform;

public sealed class FileRevealService(
    ISystemPlatform platform,
    IProcessRunner processRunner) : IFileRevealService
{
    public async Task<FileRevealResult> RevealAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return FileRevealResult.Failure("The downloaded file no longer exists.");
        }
        var parent = Path.GetDirectoryName(fullPath)!;
        var request = platform.OperatingSystem switch
        {
            OperatingSystemFamily.Windows => new ProcessLaunchRequest(
                platform.FindExecutable("explorer.exe") ?? "explorer.exe",
                [$"/select,{fullPath}"],
                parent),
            OperatingSystemFamily.MacOs => new ProcessLaunchRequest(
                platform.FindExecutable("open") ?? "/usr/bin/open",
                ["-R", fullPath],
                parent),
            OperatingSystemFamily.Linux => new ProcessLaunchRequest(
                platform.FindExecutable("xdg-open") ?? "xdg-open",
                [parent],
                parent),
            _ => throw new PlatformNotSupportedException("Reveal in folder is unavailable."),
        };
        try
        {
            await processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            return FileRevealResult.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return FileRevealResult.Failure($"The containing folder could not be opened: {exception.Message}");
        }
    }
}
