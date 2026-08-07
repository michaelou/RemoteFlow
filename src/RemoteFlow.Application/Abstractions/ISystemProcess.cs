using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Application.Abstractions;

public enum OperatingSystemFamily
{
    Windows = 1,
    MacOs = 2,
    Linux = 3,
}

public interface ISystemPlatform
{
    OperatingSystemFamily OperatingSystem { get; }

    string CurrentDirectory { get; }

    string HomeDirectory { get; }

    string? GetEnvironmentVariable(string name);

    string? FindExecutable(string name);

    bool FileExists(string path);

    string? GetLoginShellFromPasswd();
}

public sealed record ProcessLaunchRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

public interface IProcessRunner
{
    Task RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default);
}

public sealed record SystemTerminalLaunchResult(bool Succeeded, string? ErrorMessage = null)
{
    public static SystemTerminalLaunchResult Success { get; } = new(true);

    public static SystemTerminalLaunchResult Failure(string message)
    {
        return new(false, message);
    }
}

public interface ISystemTerminalLauncher
{
    Task<SystemTerminalLaunchResult> OpenLocalAsync(
        global::RemoteFlow.Application.Services.ShellProfile profile,
        CancellationToken cancellationToken = default);

    Task<SystemTerminalLaunchResult> OpenSshAsync(
        Connection connection,
        CancellationToken cancellationToken = default);
}
