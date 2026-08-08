using System.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Infrastructure.Platform;

public sealed class FileEditorLauncher(
    ISystemPlatform platform,
    IProcessRunner processRunner) : IFileEditorLauncher
{
    private const int _errorNoAssociation = 1155;
    private const int _errorCancelled = 1223;

    public async Task OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The local editing copy was not found.", fullPath);
        }

        switch (platform.OperatingSystem)
        {
            case OperatingSystemFamily.Windows:
                await OpenWithWindowsShellAsync(fullPath, cancellationToken).ConfigureAwait(false);
                return;
            case OperatingSystemFamily.MacOs:
                await OpenWithAsync(platform.FindExecutable("open") ?? "/usr/bin/open", fullPath, cancellationToken)
                    .ConfigureAwait(false);
                return;
            case OperatingSystemFamily.Linux:
                await OpenWithAsync(platform.FindExecutable("xdg-open") ?? "xdg-open", fullPath, cancellationToken)
                    .ConfigureAwait(false);
                return;
            default:
                throw new PlatformNotSupportedException("No default editor launcher is available.");
        }
    }

    private Task OpenWithAsync(string opener, string fullPath, CancellationToken cancellationToken)
    {
        return processRunner.RunAsync(
            new ProcessLaunchRequest(opener, [fullPath], Path.GetDirectoryName(fullPath)),
            cancellationToken);
    }

    private async Task OpenWithWindowsShellAsync(string fullPath, CancellationToken cancellationToken)
    {
        var workingDirectory = Path.GetDirectoryName(fullPath);
        try
        {
            await ShellExecuteAsync(fullPath, workingDirectory, verb: null, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == _errorNoAssociation)
        {
            // A file the shell has no association for (one without an extension, typically) has no
            // default editor. Ask for the "Open with" picker rather than failing the edit.
            await ShellExecuteAsync(fullPath, workingDirectory, verb: "openas", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ShellExecuteAsync(
        string fullPath,
        string? workingDirectory,
        string? verb,
        CancellationToken cancellationToken)
    {
        try
        {
            await processRunner.RunAsync(
                new ProcessLaunchRequest(fullPath, [], workingDirectory, UseShellExecute: true, Verb: verb),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == _errorCancelled)
        {
            // The user dismissed the shell's editor picker. The local copy stays watched, so they can
            // still open it themselves; reporting a failure here would only be noise.
        }
    }
}
