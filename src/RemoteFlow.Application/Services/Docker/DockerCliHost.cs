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
        if (result.IsFailure)
        {
            return DockerResult<IReadOnlyList<DockerContainer>>.Fail(result.Failure);
        }

        var listed = Parse(result.Value, DockerCli.ParseContainers);
        return listed.IsFailure
            ? listed
            : DockerResult<IReadOnlyList<DockerContainer>>.Success(
                await WithNetworksAsync(listed.Value, cancellationToken).ConfigureAwait(false));
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

    public async Task<DockerResult<IReadOnlyList<DockerImage>>> ListImagesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(DockerCli.ListImagesCommand, cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? DockerResult<IReadOnlyList<DockerImage>>.Fail(result.Failure)
            : Parse(result.Value, DockerCli.ParseImages);
    }

    public async Task<DockerResult> RemoveImageAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (!DockerCli.IsValidImageReference(reference))
        {
            return InvalidArgument<string>($"'{reference}' is not a valid image reference");
        }

        var result = await RunAsync(DockerCli.RemoveImageCommand(reference), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? result : DockerResult.Success();
    }

    public async Task<DockerResult<IReadOnlyList<DockerVolume>>> ListVolumesAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(DockerCli.ListVolumesCommand, cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? DockerResult<IReadOnlyList<DockerVolume>>.Fail(result.Failure)
            : Parse(result.Value, DockerCli.ParseVolumes);
    }

    public async Task<DockerResult> RemoveVolumeAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!DockerCli.IsValidVolumeName(name))
        {
            return InvalidArgument<string>($"'{name}' is not a valid volume name");
        }

        var result = await RunAsync(DockerCli.RemoveVolumeCommand(name), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? result : DockerResult.Success();
    }

    public async Task<DockerResult<IReadOnlyList<DockerComposeProject>>> ListComposeProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(DockerCli.ListComposeProjectsCommand, cancellationToken).ConfigureAwait(false);
        return result.IsFailure
            ? DockerResult<IReadOnlyList<DockerComposeProject>>.Fail(result.Failure)
            : Parse(result.Value, DockerCli.ParseComposeProjects);
    }

    public async Task<DockerResult<ISshRunningCommand>> StartOperationAsync(
        DockerOperation operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (DockerCli.FindInvalidArgument(operation) is { } problem)
        {
            return InvalidArgument<ISshRunningCommand>(problem);
        }

        var started = await _connection.StartCommandAsync(DockerCli.OperationCommand(operation), cancellationToken)
            .ConfigureAwait(false);
        return started.IsFailure
            ? DockerResult<ISshRunningCommand>.Fail(DockerError.ConnectionFailed, started.Failure.Message)
            : DockerResult<ISshRunningCommand>.Success(started.Value);
    }

    public async Task<DockerResult> ValidateComposeAsync(
        IReadOnlyList<string> configFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configFiles);
        if (configFiles.Count == 0)
        {
            return InvalidArgument<string>("No compose file was given");
        }

        if (configFiles.FirstOrDefault(file => !DockerCli.IsValidComposeFile(file)) is { } invalid)
        {
            return InvalidArgument<string>($"'{invalid}' is not a usable compose file path");
        }

        var result = await RunAsync(DockerCli.ValidateComposeCommand(configFiles), cancellationToken).ConfigureAwait(false);
        return result.IsFailure ? result : DockerResult.Success();
    }

    /// <summary>Adds the addresses of the running containers. They are a detail of the list, not the list
    /// itself: when <c>docker inspect</c> fails — a container removed between the two commands is enough —
    /// what it did answer is kept, and the rest go without.</summary>
    private async Task<IReadOnlyList<DockerContainer>> WithNetworksAsync(
        IReadOnlyList<DockerContainer> containers,
        CancellationToken cancellationToken)
    {
        var running = containers
            .Where(container => container.IsRunning && DockerCli.IsValidContainerReference(container.Id))
            .Select(container => container.Id)
            .ToList();
        if (running.Count == 0)
        {
            return containers;
        }

        var executed = await _connection.ExecuteAsync(DockerCli.InspectNetworksCommand(running), cancellationToken)
            .ConfigureAwait(false);
        if (executed.IsFailure)
        {
            return containers;
        }

        IReadOnlyDictionary<string, IReadOnlyList<DockerContainerNetwork>> networks;
        try
        {
            networks = DockerCli.ParseNetworks(executed.Value.StandardOutput);
        }
        catch (JsonException)
        {
            return containers;
        }

        return [.. containers.Select(container => networks.TryGetValue(container.Id, out var attached)
            ? container with { Networks = attached }
            : container)];
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

    private static DockerResult<T> InvalidArgument<T>(string problem)
    {
        return DockerResult<T>.Fail(DockerError.InvalidArgument, $"{problem}, so nothing was sent to the server.");
    }

    private static DockerResult<T> InvalidReference<T>(string container)
    {
        return DockerResult<T>.Fail(
            DockerError.InvalidContainer,
            $"'{container}' is not a valid container name or ID, so nothing was sent to the server.");
    }
}
