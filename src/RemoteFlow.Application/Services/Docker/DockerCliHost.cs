using System.Text.Json;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Abstractions.Ssh;

namespace RemoteFlow.Application.Services.Docker;

/// <summary><see cref="IDockerHost"/> over an SSH connection someone else owns: this class sends commands on
/// it and never disposes it.</summary>
public sealed class DockerCliHost(ISshConnection connection, string? username = null) : IDockerHost
{
    private readonly ISshConnection _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<DockerResult<DockerHostInfo>> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(DockerCli.VersionCommand, cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? DockerResult<DockerHostInfo>.Fail(result.Failure)
            : DockerResult<DockerHostInfo>.Success(new DockerHostInfo(result.Value.Trim()));
    }

    public async Task<DockerResult<IReadOnlyList<DockerContainer>>> ListContainersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(DockerCli.ListContainersCommand, cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? DockerResult<IReadOnlyList<DockerContainer>>.Fail(result.Failure)
            : Parse(result.Value, DockerCli.ParseContainers);
    }

    public async Task<DockerResult<IReadOnlyList<DockerContainerStats>>> GetStatsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(DockerCli.StatsCommand, cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? DockerResult<IReadOnlyList<DockerContainerStats>>.Fail(result.Failure)
            : Parse(result.Value, DockerCli.ParseStats);
    }

    public async Task<DockerResult> RunActionAsync(
        string container,
        DockerAction action,
        CancellationToken cancellationToken = default)
    {
        if (!DockerCli.IsValidContainerReference(container))
        {
            return InvalidReference<string>(container);
        }

        var result = await RunAsync(DockerCli.ActionCommand(container, action), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? result : DockerResult.Success();
    }

    public async Task<DockerResult<ISshRunningCommand>> OpenLogsAsync(
        string container,
        DockerLogOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!DockerCli.IsValidContainerReference(container))
        {
            return InvalidReference<ISshRunningCommand>(container);
        }

        var started = await _connection.StartCommandAsync(DockerCli.LogsCommand(container, options), cancellationToken)
            .ConfigureAwait(false);
        return started.IsFailure
            ? DockerResult<ISshRunningCommand>.Fail(DockerError.ConnectionFailed, started.Failure.Message)
            : DockerResult<ISshRunningCommand>.Success(started.Value);
    }

    private async Task<DockerResult<string>> RunAsync(string command, CancellationToken cancellationToken)
    {
        var executed = await _connection.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (executed.IsFailure)
        {
            return DockerResult<string>.Fail(DockerError.ConnectionFailed, executed.Failure.Message);
        }

        var output = executed.Value;
        return output.ExitCode == 0
            ? DockerResult<string>.Success(output.StandardOutput)
            : DockerResult<string>.Fail(DockerCli.DescribeFailure(output.ExitCode, output.StandardError, username));
    }

    private static DockerResult<IReadOnlyList<T>> Parse<T>(string output, Func<string, IReadOnlyList<T>> parse)
    {
        try
        {
            return DockerResult<IReadOnlyList<T>>.Success(parse(output));
        }
        catch (JsonException exception)
        {
            return DockerResult<IReadOnlyList<T>>.Fail(
                DockerError.UnreadableOutput,
                $"Docker's answer could not be read: {exception.Message}");
        }
    }

    private static DockerResult<T> InvalidReference<T>(string container)
    {
        return DockerResult<T>.Fail(
            DockerError.InvalidContainer,
            $"'{container}' is not a valid container name or ID, so nothing was sent to the server.");
    }
}
