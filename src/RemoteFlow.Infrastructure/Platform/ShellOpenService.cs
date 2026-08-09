using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Platform;

/// <summary>Hands a folder to the desktop file manager, or a link to the browser, using the same
/// per-platform commands as <see cref="FileRevealService"/>.</summary>
public sealed class ShellOpenService(
    ISystemPlatform platform,
    IProcessRunner processRunner) : IShellOpenService
{
    public async Task<ShellOpenResult> OpenFolderAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            // The log directory exists from the first log line onward and the data directory from the
            // first migration, so this is close to unreachable — but a directory the user deleted while
            // the application was running should say so rather than open nothing.
            return ShellOpenResult.Failure($"{fullPath} does not exist.");
        }

        var request = platform.OperatingSystem switch
        {
            OperatingSystemFamily.Windows => new ProcessLaunchRequest(
                platform.FindExecutable("explorer.exe") ?? "explorer.exe",
                [fullPath],
                fullPath),
            OperatingSystemFamily.MacOs => new ProcessLaunchRequest(
                platform.FindExecutable("open") ?? "/usr/bin/open",
                [fullPath],
                fullPath),
            OperatingSystemFamily.Linux => new ProcessLaunchRequest(
                platform.FindExecutable("xdg-open") ?? "xdg-open",
                [fullPath],
                fullPath),
            _ => throw new PlatformNotSupportedException("Opening a folder is unavailable."),
        };

        return await RunAsync(request, $"{fullPath} could not be opened", cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ShellOpenResult> OpenUrlAsync(
        Uri url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            // Windows will happily shell-execute a file: or ms-settings: URI, and on Linux xdg-open will
            // launch whatever claims the scheme. The only links RemoteFlow opens are its own web pages,
            // so nothing else needs to be allowed.
            return ShellOpenResult.Failure("Only http and https links can be opened.");
        }

        var request = platform.OperatingSystem switch
        {
            // UseShellExecute is what makes the default browser handle it; explorer.exe would work too,
            // but through a process that outlives the launch and confuses the caller.
            OperatingSystemFamily.Windows => new ProcessLaunchRequest(
                url.AbsoluteUri,
                [],
                UseShellExecute: true),
            OperatingSystemFamily.MacOs => new ProcessLaunchRequest(
                platform.FindExecutable("open") ?? "/usr/bin/open",
                [url.AbsoluteUri]),
            OperatingSystemFamily.Linux => new ProcessLaunchRequest(
                platform.FindExecutable("xdg-open") ?? "xdg-open",
                [url.AbsoluteUri]),
            _ => throw new PlatformNotSupportedException("Opening a link is unavailable."),
        };

        return await RunAsync(request, $"{url} could not be opened", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ShellOpenResult> RunAsync(
        ProcessLaunchRequest request,
        string failurePrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            await processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            return ShellOpenResult.Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ShellOpenResult.Failure($"{failurePrefix}: {exception.Message}");
        }
    }
}
