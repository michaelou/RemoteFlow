using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Domain.ValueObjects;

public sealed class SshOptions
{
    private SshOptions()
    {
        TerminalType = "xterm-256color";
        HostKeyPolicy = HostKeyPolicy.Strict;
        RequestPty = true;
    }

    public int? KeepAliveSeconds { get; private set; }

    public string TerminalType { get; private set; }

    public string? PrivateKeyPath { get; private set; }

    public string? InitialCommand { get; private set; }

    public string? StartupDirectory { get; private set; }

    public HostKeyPolicy HostKeyPolicy { get; private set; }

    public bool RequestPty { get; private set; }

    public static SshOptions Default()
    {
        return new();
    }

    public Result<SshOptions> Configure(
        int? keepAliveSeconds = null,
        string? terminalType = null,
        string? privateKeyPath = null,
        string? initialCommand = null,
        string? startupDirectory = null,
        HostKeyPolicy hostKeyPolicy = HostKeyPolicy.Strict,
        bool requestPty = true)
    {
        if (keepAliveSeconds is <= 0)
        {
            return Result<SshOptions>.Failure(RemoteFlowError.Validation(
                "ssh.keep_alive",
                "Keep-alive seconds must be greater than zero when specified."));
        }

        var normalizedTerminal = DomainValidation.Required(
            terminalType ?? "xterm-256color",
            100,
            "ssh.terminal_type",
            out var error);
        if (error is not null)
        {
            return Result<SshOptions>.Failure(error);
        }

        if (!Enum.IsDefined(hostKeyPolicy))
        {
            return Result<SshOptions>.Failure(RemoteFlowError.Validation("ssh.host_key_policy", "The host key policy is invalid."));
        }

        KeepAliveSeconds = keepAliveSeconds;
        TerminalType = normalizedTerminal!;
        PrivateKeyPath = string.IsNullOrWhiteSpace(privateKeyPath) ? null : privateKeyPath.Trim();
        InitialCommand = string.IsNullOrWhiteSpace(initialCommand) ? null : initialCommand.Trim();
        StartupDirectory = string.IsNullOrWhiteSpace(startupDirectory) ? null : startupDirectory.Trim();
        HostKeyPolicy = hostKeyPolicy;
        RequestPty = requestPty;
        return Result<SshOptions>.Success(this);
    }
}
