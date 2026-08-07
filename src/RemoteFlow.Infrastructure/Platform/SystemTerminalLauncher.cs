using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Infrastructure.Platform;

public sealed class SystemTerminalLauncher(ISystemPlatform platform, IProcessRunner processRunner) : ISystemTerminalLauncher
{
    private readonly ISystemPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    private readonly IProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    public Task<SystemTerminalLaunchResult> OpenLocalAsync(
        ShellProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return LaunchAsync(
            profile.ShellPath,
            profile.Arguments,
            profile.WorkingDirectory,
            profile.EnvironmentVariables,
            cancellationToken);
    }

    public Task<SystemTerminalLaunchResult> OpenSshAsync(
        Connection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.Protocol is not (ProtocolType.Ssh or ProtocolType.Sftp))
        {
            return Task.FromResult(SystemTerminalLaunchResult.Failure("Only SSH and SFTP connections can open through the system SSH client."));
        }

        var ssh = _platform.FindExecutable(_platform.OperatingSystem == OperatingSystemFamily.Windows ? "ssh.exe" : "ssh");
        if (ssh is null)
        {
            return Task.FromResult(SystemTerminalLaunchResult.Failure("The system SSH client was not found. Install OpenSSH and ensure 'ssh' is on PATH."));
        }

        var arguments = new List<string>();
        if (connection.Port != 22)
        {
            arguments.AddRange(["-p", connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (!string.IsNullOrWhiteSpace(connection.Ssh.PrivateKeyPath))
        {
            arguments.AddRange(["-i", connection.Ssh.PrivateKeyPath]);
        }

        var target = string.IsNullOrWhiteSpace(connection.Username)
            ? connection.Host
            : $"{connection.Username}@{connection.Host}";
        arguments.Add(target);
        return LaunchAsync(ssh, arguments, _platform.HomeDirectory, null, cancellationToken);
    }

    private async Task<SystemTerminalLaunchResult> LaunchAsync(
        string command,
        IReadOnlyList<string> commandArguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = BuildTerminalRequest(command, commandArguments, workingDirectory, environment);
            if (request is null)
            {
                return SystemTerminalLaunchResult.Failure(NoTerminalMessage());
            }

            await _processRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            return SystemTerminalLaunchResult.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return SystemTerminalLaunchResult.Failure($"The system terminal could not be opened: {exception.Message}");
        }
    }

    private ProcessLaunchRequest? BuildTerminalRequest(
        string command,
        IReadOnlyList<string> commandArguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        return _platform.OperatingSystem switch
        {
            OperatingSystemFamily.Windows => BuildWindowsRequest(command, commandArguments, workingDirectory, environment),
            OperatingSystemFamily.MacOs => BuildMacRequest(command, commandArguments, workingDirectory, environment),
            OperatingSystemFamily.Linux => BuildLinuxRequest(command, commandArguments, workingDirectory, environment),
            _ => null,
        };
    }

    private ProcessLaunchRequest? BuildWindowsRequest(
        string command,
        IReadOnlyList<string> commandArguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
#pragma warning disable IDE0046 // Keep the documented wt -> pwsh -> conhost precedence readable.
        if (_platform.FindExecutable("wt.exe") is { } windowsTerminal)
        {
            return new ProcessLaunchRequest(
                windowsTerminal,
                ["-d", workingDirectory, command, .. commandArguments],
                workingDirectory,
                environment);
        }

        if (_platform.FindExecutable("pwsh.exe") is { } powerShell)
        {
            return new ProcessLaunchRequest(
                powerShell,
                ["-NoExit", "-Command", "&", command, .. commandArguments],
                workingDirectory,
                environment);
        }

        var request = _platform.FindExecutable("conhost.exe") is { } conhost
            ? new ProcessLaunchRequest(conhost, [command, .. commandArguments], workingDirectory, environment)
            : null;
        return request;
#pragma warning restore IDE0046
    }

    private ProcessLaunchRequest? BuildMacRequest(
        string command,
        IReadOnlyList<string> commandArguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        var application = _platform.FileExists("/Applications/Terminal.app")
            ? "Terminal"
            : _platform.FileExists("/Applications/iTerm.app")
                ? "iTerm"
                : null;
        var open = _platform.FindExecutable("open") ?? (_platform.FileExists("/usr/bin/open") ? "/usr/bin/open" : null);
        return application is not null && open is not null
            ? new ProcessLaunchRequest(open, ["-a", application, "--args", command, .. commandArguments], workingDirectory, environment)
            : null;
    }

    private ProcessLaunchRequest? BuildLinuxRequest(
        string command,
        IReadOnlyList<string> commandArguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        var configured = _platform.GetEnvironmentVariable("TERMINAL");
        var candidates = new[]
        {
            configured,
            "x-terminal-emulator",
            "gnome-terminal",
            "konsole",
            "alacritty",
            "xterm",
        };
        var terminal = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => _platform.FindExecutable(candidate!))
            .FirstOrDefault(candidate => candidate is not null);
        if (terminal is null)
        {
            return null;
        }

        var name = Path.GetFileName(terminal);
        var separator = name.Equals("konsole", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("alacritty", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("xterm", StringComparison.OrdinalIgnoreCase)
                ? "-e"
                : "--";
        return new ProcessLaunchRequest(terminal, [separator, command, .. commandArguments], workingDirectory, environment);
    }

    private string NoTerminalMessage()
    {
        return _platform.OperatingSystem switch
        {
            OperatingSystemFamily.Windows => "No system terminal was found. Install Windows Terminal or PowerShell 7, or enable conhost.exe.",
            OperatingSystemFamily.MacOs => "No system terminal was found. Install Terminal.app or iTerm2.",
            OperatingSystemFamily.Linux => "No terminal emulator was found. Install x-terminal-emulator, GNOME Terminal, Konsole, Alacritty, or xterm.",
            _ => "No supported system terminal was found.",
        };
    }
}
