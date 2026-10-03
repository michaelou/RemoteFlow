using RemoteFlow.Application.Abstractions.Ssh;

namespace RemoteFlow.Application.Abstractions.Docker;

public enum DockerError
{
    /// <summary>There is no <c>docker</c> on the remote <c>PATH</c>.</summary>
    NotInstalled = 1,

    /// <summary>The CLI is there but the account cannot reach the daemon's socket — almost always a user who
    /// is not in the <c>docker</c> group.</summary>
    PermissionDenied = 2,

    /// <summary>The CLI is there but the daemon is not running, or not where the CLI looks for it.</summary>
    DaemonUnavailable = 3,

    NoSuchContainer = 4,

    /// <summary>The name or ID did not pass validation, so no command was sent.</summary>
    InvalidContainer = 5,

    /// <summary>Docker ran and refused, for a reason only its own message explains.</summary>
    CommandFailed = 6,

    /// <summary>The SSH channel failed underneath the command.</summary>
    ConnectionFailed = 7,

    /// <summary>Docker answered with something that could not be read.</summary>
    UnreadableOutput = 8,

    /// <summary>Docker is there but the Compose v2 plugin (<c>docker compose</c>) is not.</summary>
    ComposeNotInstalled = 9,

    /// <summary>An image reference, volume name, project name or file path did not pass validation, so no
    /// command was sent.</summary>
    InvalidArgument = 10,
}

public sealed record DockerFailure(DockerError Error, string Message);

public class DockerResult
{
    protected DockerResult(DockerFailure? failure)
    {
        FailureValue = failure;
    }

    public bool IsSuccess => FailureValue is null;

    public bool IsFailure => FailureValue is not null;

    public DockerFailure Failure => FailureValue ??
        throw new InvalidOperationException("A successful Docker result has no failure.");

    protected DockerFailure? FailureValue { get; }

    public static DockerResult Success()
    {
        return new(null);
    }

    public static DockerResult Fail(DockerError error, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(new DockerFailure(error, message));
    }
}

public sealed class DockerResult<T> : DockerResult
{
    private DockerResult(T value) : base(null)
    {
        SuccessfulValue = value;
    }

    private DockerResult(DockerFailure failure) : base(failure) { }

    public T Value => IsSuccess
        ? SuccessfulValue!
        : throw new InvalidOperationException("A failed Docker result has no value.");

    private T? SuccessfulValue { get; }

#pragma warning disable CA1000 // Result factories intentionally live on the result type.
    public static DockerResult<T> Success(T value)
    {
        return new(value);
    }

    public static new DockerResult<T> Fail(DockerError error, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(new DockerFailure(error, message));
    }

    public static DockerResult<T> Fail(DockerFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new(failure);
    }
#pragma warning restore CA1000
}

/// <summary>The lifecycle states <c>docker ps</c> reports. Anything a newer engine invents reads as
/// <see cref="Unknown"/> rather than failing the whole list.</summary>
public enum DockerContainerState
{
    Unknown = 0,
    Created = 1,
    Running = 2,
    Paused = 3,
    Restarting = 4,
    Removing = 5,
    Exited = 6,
    Dead = 7,
}

/// <summary>One row of <c>docker ps -a</c>. The compose labels are read individually rather than parsed out
/// of the comma-joined label list, which cannot be split safely once a value contains a comma.
/// <paramref name="Volumes"/> names the volumes the container mounts; bind mounts are not volumes and do
/// not appear.</summary>
public sealed record DockerContainer(
    string Id,
    string Name,
    string Image,
    DockerContainerState State,
    string Status,
    string Ports,
    string CreatedAt,
    string? ComposeProject,
    string? ComposeService,
    IReadOnlyList<string>? Volumes = null)
{
    public bool IsRunning => State is DockerContainerState.Running or DockerContainerState.Restarting;

    public IReadOnlyList<string> VolumeNames => Volumes ?? [];
}

/// <summary>One row of <c>docker image ls</c>. An untagged image has a null repository and tag.
/// <paramref name="Containers"/> is how many containers use it, when the engine reports that — older
/// engines answer "N/A" outside <c>docker system df</c>, and that reads as null.</summary>
public sealed record DockerImage(
    string Id,
    string? Repository,
    string? Tag,
    string Size,
    string CreatedSince,
    int? Containers)
{
    /// <summary>What to call it, and what to remove it by: <c>repository:tag</c> when it has one, so removing
    /// a row removes that tag and not every tag the image carries; otherwise the ID.</summary>
    public string Reference => Repository is null ? Id : $"{Repository}:{Tag ?? "latest"}";
}

/// <summary>One row of <c>docker volume ls</c>.</summary>
public sealed record DockerVolume(string Name, string Driver, string? ComposeProject);

/// <summary>One row of <c>docker compose ls --all</c>: a project that has containers, running or not.
/// A project brought down has none, and drops off this list.</summary>
public sealed record DockerComposeProject(string Name, string Status, IReadOnlyList<string> ConfigFiles);

/// <summary>One row of <c>docker stats --no-stream</c>: a single sample, not a live feed. The usage columns
/// stay as Docker formats them — "12.3MiB / 1.94GiB" — because the units vary row to row and re-deriving
/// them would only lose precision.</summary>
public sealed record DockerContainerStats(
    string Id,
    string Name,
    double? CpuPercent,
    double? MemoryPercent,
    string MemoryUsage,
    string NetworkIo,
    string BlockIo);

public sealed record DockerHostInfo(string ServerVersion);

public enum DockerAction
{
    Start = 0,
    Stop = 1,
    Restart = 2,
    Remove = 3,
}

public sealed record DockerLogOptions
{
    /// <summary>How many lines of history to send before following. Zero sends none.</summary>
    public int Tail { get; init; } = 500;

    /// <summary>Keep the command running and stream new lines as the container writes them.</summary>
    public bool Follow { get; init; }

    public bool Timestamps { get; init; }
}

/// <summary>The commands that can run for minutes — a compose up that pulls, an image pull, a prune — and
/// so run as a streamed command rather than under the operation timeout.</summary>
public abstract record DockerOperation
{
    private DockerOperation() { }

    public sealed record ComposeUp(string? Project, IReadOnlyList<string> ConfigFiles) : DockerOperation;

    public sealed record ComposeDown(string Project, bool RemoveVolumes) : DockerOperation;

    public sealed record PullImage(string Reference) : DockerOperation;

    public sealed record PruneImages : DockerOperation;

    public sealed record PruneVolumes : DockerOperation;
}

/// <summary>Docker driven through its CLI over an SSH connection that is already open. Nothing is installed on
/// the server and the Engine API is never exposed; see ADR-0026.</summary>
public interface IDockerHost
{
    Task<DockerResult<DockerHostInfo>> ProbeAsync(CancellationToken cancellationToken = default);

    Task<DockerResult<IReadOnlyList<DockerContainer>>> ListContainersAsync(
        CancellationToken cancellationToken = default);

    /// <summary>One sample for every running container. Docker spends a second or two measuring CPU, so this
    /// is slower than the list and is requested separately.</summary>
    Task<DockerResult<IReadOnlyList<DockerContainerStats>>> GetStatsAsync(
        CancellationToken cancellationToken = default);

    Task<DockerResult> RunActionAsync(
        string container,
        DockerAction action,
        CancellationToken cancellationToken = default);

    /// <summary>Starts <c>docker logs</c> and hands back the running command. The caller owns it: disposing it
    /// is what stops a followed log on the server.</summary>
    Task<DockerResult<ISshRunningCommand>> OpenLogsAsync(
        string container,
        DockerLogOptions options,
        CancellationToken cancellationToken = default);

    Task<DockerResult<IReadOnlyList<DockerImage>>> ListImagesAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes one image by its <see cref="DockerImage.Reference"/>. Never forced: an image a
    /// container still uses is refused by Docker, and that refusal is the answer.</summary>
    Task<DockerResult> RemoveImageAsync(string reference, CancellationToken cancellationToken = default);

    Task<DockerResult<IReadOnlyList<DockerVolume>>> ListVolumesAsync(CancellationToken cancellationToken = default);

    Task<DockerResult> RemoveVolumeAsync(string name, CancellationToken cancellationToken = default);

    Task<DockerResult<IReadOnlyList<DockerComposeProject>>> ListComposeProjectsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Starts a long-running command and hands back its output. As with logs, the caller owns the
    /// running command.</summary>
    Task<DockerResult<ISshRunningCommand>> StartOperationAsync(
        DockerOperation operation,
        CancellationToken cancellationToken = default);
}

/// <summary>A compose project RemoteFlow has seen on a server, with the files it was brought up from — the
/// one thing <c>docker compose ls</c> stops reporting once the project is down, and the one thing needed
/// to bring it back up.</summary>
public sealed record RememberedComposeProject(Guid ConnectionId, string Name, string[] ConfigFiles);

public interface IDockerComposeProjectMemory
{
    Task<IReadOnlyList<RememberedComposeProject>> RecallAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);

    /// <summary>Records the projects just listed, replacing what was known about each by name. Projects not
    /// in the list are kept: not being listed is exactly what being down looks like.</summary>
    Task RememberAsync(
        Guid connectionId,
        IReadOnlyList<DockerComposeProject> projects,
        CancellationToken cancellationToken = default);

    Task ForgetAsync(Guid connectionId, string name, CancellationToken cancellationToken = default);
}
