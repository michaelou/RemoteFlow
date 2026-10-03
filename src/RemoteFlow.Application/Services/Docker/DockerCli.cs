using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteFlow.Application.Abstractions.Docker;

namespace RemoteFlow.Application.Services.Docker;

/// <summary>Every <c>docker</c> command line RemoteFlow sends, and the readers for what comes back. Kept
/// apart from the SSH plumbing so that the one place a container name meets a shell can be read — and
/// tested — on its own.
///
/// Two rules keep a name from becoming a command: a reference must match Docker's own name grammar before
/// it is used at all, and it is single-quoted anyway. Either alone would do; both cost nothing.</summary>
public static partial class DockerCli
{
    /// <summary>The <c>docker ps</c> row as one JSON object per line. The compose labels are asked for by
    /// name: the <c>Labels</c> column is a comma-joined string that cannot be split safely.</summary>
    private const string _listFormat =
        "{\"id\":{{json .ID}},\"name\":{{json .Names}},\"image\":{{json .Image}},\"state\":{{json .State}}," +
        "\"status\":{{json .Status}},\"ports\":{{json .Ports}},\"created\":{{json .CreatedAt}}," +
        "\"project\":{{json (.Label \"com.docker.compose.project\")}}," +
        "\"service\":{{json (.Label \"com.docker.compose.service\")}},\"mounts\":{{json .Mounts}}}";

    /// <summary>The <c>docker volume ls</c> row, with the compose project asked for by name for the same
    /// reason as above.</summary>
    private const string _volumeFormat =
        "{\"name\":{{json .Name}},\"driver\":{{json .Driver}}," +
        "\"project\":{{json (.Label \"com.docker.compose.project\")}}}";

    /// <summary>The command that replaces the login shell of a fresh terminal with a shell inside the
    /// container: bash when the image has it, sh when it does not. <c>exec</c>, so leaving the container
    /// ends the tab instead of dropping back to the host without a word.</summary>
    private const string _shellProbe = "command -v bash >/dev/null 2>&1 && exec bash || exec sh";

    /// <summary><c>--no-trunc</c> because the mounts column is otherwise shortened, and a shortened
    /// anonymous volume name matches nothing on the Volumes tab.</summary>
    public static string ListContainersCommand { get; } = $"docker ps --all --no-trunc --format {Quote(_listFormat)}";

    public static string ListImagesCommand { get; } = "docker image ls --format '{{json .}}'";

    public static string ListVolumesCommand { get; } = $"docker volume ls --format {Quote(_volumeFormat)}";

    public static string ListComposeProjectsCommand { get; } = "docker compose ls --all --format json";

    public static string StatsCommand { get; } = "docker stats --no-stream --format '{{json .}}'";

    public static string VersionCommand { get; } = "docker version --format '{{.Server.Version}}'";

    /// <summary>Docker's own grammar for a container name, which also admits every ID, full or short.</summary>
    public static bool IsValidContainerReference(string? reference)
    {
        return !string.IsNullOrEmpty(reference) && reference.Length <= 255 && ContainerReference().IsMatch(reference);
    }

    /// <summary>An image as <c>docker pull</c> and <c>docker image rm</c> take it: an optional registry host
    /// and port, a path, and a tag or digest — or a bare image ID.</summary>
    public static bool IsValidImageReference(string? reference)
    {
        return !string.IsNullOrEmpty(reference) && reference.Length <= 512 && ImageReference().IsMatch(reference);
    }

    /// <summary>Volume names follow the same grammar as container names.</summary>
    public static bool IsValidVolumeName(string? name)
    {
        return IsValidContainerReference(name);
    }

    /// <summary>Compose's own grammar for a project name: lower case, digits, dashes and underscores.</summary>
    public static bool IsValidProjectName(string? name)
    {
        return !string.IsNullOrEmpty(name) && name.Length <= 255 && ProjectName().IsMatch(name);
    }

    /// <summary>A compose file path is free text, so it is only ever quoted; a control character has no
    /// business in one, so those are refused rather than carried into a command line.</summary>
    public static bool IsValidComposeFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && path.Length <= 4096 && !path.Any(char.IsControl);
    }

    /// <summary>POSIX single quoting: the only character that needs care inside single quotes is the quote
    /// itself, which closes the string, adds an escaped quote, and opens it again.</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    public static string ActionCommand(string container, DockerAction action)
    {
        var verb = action switch
        {
            DockerAction.Start => "start",
            DockerAction.Stop => "stop",
            DockerAction.Restart => "restart",
            DockerAction.Remove => "rm",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        return $"docker {verb} {QuoteReference(container)}";
    }

    public static string LogsCommand(string container, DockerLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.Tail);
        var command = new StringBuilder("docker logs --tail ")
            .Append(options.Tail.ToString(CultureInfo.InvariantCulture));
        if (options.Follow)
        {
            _ = command.Append(" --follow");
        }

        if (options.Timestamps)
        {
            _ = command.Append(" --timestamps");
        }

        return command.Append(' ').Append(QuoteReference(container)).ToString();
    }

    public static string ShellCommand(string container)
    {
        return $"exec docker exec -it {QuoteReference(container)} sh -c {Quote(_shellProbe)}";
    }

    public static string RemoveImageCommand(string reference)
    {
        return IsValidImageReference(reference)
            ? $"docker image rm {Quote(reference)}"
            : throw new ArgumentException($"'{reference}' is not a valid image reference.", nameof(reference));
    }

    public static string RemoveVolumeCommand(string name)
    {
        return IsValidVolumeName(name)
            ? $"docker volume rm {Quote(name)}"
            : throw new ArgumentException($"'{name}' is not a valid volume name.", nameof(name));
    }

    /// <summary>The command line for a long-running operation. Compose is given every file with its own
    /// <c>-f</c>, in the order <c>docker compose ls</c> reported them: the first one's folder is the project
    /// directory, which is where compose looks for <c>.env</c>.</summary>
    public static string OperationCommand(DockerOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation switch
        {
            DockerOperation.ComposeUp up => ComposeUpCommand(up),
            DockerOperation.ComposeDown down => $"docker compose -p {QuoteProject(down.Project)} down" +
                (down.RemoveVolumes ? " --volumes" : string.Empty),
            DockerOperation.PullImage pull => IsValidImageReference(pull.Reference)
                ? $"docker pull {Quote(pull.Reference)}"
                : throw new ArgumentException($"'{pull.Reference}' is not a valid image reference.", nameof(operation)),
            DockerOperation.PruneImages => "docker image prune --force",
            DockerOperation.PruneVolumes => "docker volume prune --force",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
    }

    /// <summary>What is wrong with an operation's arguments, in words, or null when nothing is.</summary>
    public static string? FindInvalidArgument(DockerOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation switch
        {
            DockerOperation.ComposeUp { Project: { } project } when !IsValidProjectName(project) =>
                $"'{project}' is not a valid compose project name",
            DockerOperation.ComposeUp { ConfigFiles.Count: 0 } => "No compose file was given",
            DockerOperation.ComposeUp up when up.ConfigFiles.FirstOrDefault(file => !IsValidComposeFile(file)) is { } file =>
                $"'{file}' is not a usable compose file path",
            DockerOperation.ComposeDown down when !IsValidProjectName(down.Project) =>
                $"'{down.Project}' is not a valid compose project name",
            DockerOperation.PullImage pull when !IsValidImageReference(pull.Reference) =>
                $"'{pull.Reference}' is not a valid image reference",
            _ => null,
        };
    }

    public static IReadOnlyList<DockerContainer> ParseContainers(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var containers = new List<DockerContainer>();
        foreach (var line in JsonLines(output))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            containers.Add(new DockerContainer(
                Text(row, "id"),
                Text(row, "name"),
                Text(row, "image"),
                ParseState(Text(row, "state")),
                Text(row, "status"),
                Text(row, "ports"),
                Text(row, "created"),
                OptionalText(row, "project"),
                OptionalText(row, "service"),
                SplitList(OptionalText(row, "mounts"))));
        }

        return containers;
    }

    public static IReadOnlyList<DockerImage> ParseImages(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var images = new List<DockerImage>();
        foreach (var line in JsonLines(output))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            images.Add(new DockerImage(
                Text(row, "ID"),
                NoneAsNull(OptionalText(row, "Repository")),
                NoneAsNull(OptionalText(row, "Tag")),
                Text(row, "Size"),
                Text(row, "CreatedSince"),
                int.TryParse(Text(row, "Containers"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                    ? count
                    : null));
        }

        return images;
    }

    public static IReadOnlyList<DockerVolume> ParseVolumes(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var volumes = new List<DockerVolume>();
        foreach (var line in JsonLines(output))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            volumes.Add(new DockerVolume(Text(row, "name"), Text(row, "driver"), OptionalText(row, "project")));
        }

        return volumes;
    }

    /// <summary><c>docker compose ls --format json</c> writes one JSON array, not a line per project.</summary>
    public static IReadOnlyList<DockerComposeProject> ParseComposeProjects(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var trimmed = output.Trim();
        if (trimmed.Length == 0)
        {
            return [];
        }

        using var document = JsonDocument.Parse(trimmed);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? [.. document.RootElement.EnumerateArray().Select(row => new DockerComposeProject(
                Text(row, "Name"),
                Text(row, "Status"),
                SplitList(OptionalText(row, "ConfigFiles"))))]
            : throw new JsonException("Expected a JSON array of compose projects.");
    }

    public static IReadOnlyList<DockerContainerStats> ParseStats(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var stats = new List<DockerContainerStats>();
        foreach (var line in JsonLines(output))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var id = Text(row, "ID");
            stats.Add(new DockerContainerStats(
                string.IsNullOrEmpty(id) ? Text(row, "Container") : id,
                Text(row, "Name"),
                ParsePercent(Text(row, "CPUPerc")),
                ParsePercent(Text(row, "MemPerc")),
                Text(row, "MemUsage"),
                Text(row, "NetIO"),
                Text(row, "BlockIO")));
        }

        return stats;
    }

    /// <summary>Turns a non-zero exit into something a person can act on. The messages Docker prints for the
    /// common cases are stable across versions, so matching them is safer than it looks — and anything
    /// unrecognised is passed through verbatim rather than guessed at.</summary>
    public static DockerFailure DescribeFailure(int exitCode, string standardError, string? username = null)
    {
        var message = (standardError ?? string.Empty).Trim();
        if (exitCode == 127
            || message.Contains("command not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("docker: not found", StringComparison.OrdinalIgnoreCase))
        {
            return new(DockerError.NotInstalled, "Docker is not installed on this server, or it is not on the PATH of a non-interactive SSH session.");
        }

        if (message.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            && message.Contains("docker", StringComparison.OrdinalIgnoreCase)
            && message.Contains("sock", StringComparison.OrdinalIgnoreCase))
        {
            var account = string.IsNullOrWhiteSpace(username) ? "This account" : $"The account '{username}'";
            return new(
                DockerError.PermissionDenied,
                $"{account} is not allowed to use Docker. Add it to the docker group on the server " +
                $"(sudo usermod -aG docker {(string.IsNullOrWhiteSpace(username) ? "<user>" : username)}) and connect again.");
        }

        // The first two are the wording up to Docker 28; Docker 29 says "failed to connect to the docker API".
        if (message.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase)
            || message.Contains("failed to connect to the docker API", StringComparison.OrdinalIgnoreCase))
        {
            return new(DockerError.DaemonUnavailable, "Docker is installed but its daemon is not running on this server.");
        }

        if (message.Contains("'compose' is not a docker command", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unknown command \"compose\"", StringComparison.OrdinalIgnoreCase))
        {
            return new(DockerError.ComposeNotInstalled, "The Docker Compose plugin (docker compose) is not installed on this server.");
        }

        if (message.Contains("No such container", StringComparison.OrdinalIgnoreCase))
        {
            return new(DockerError.NoSuchContainer, "The container no longer exists. Refresh the list.");
        }

        var firstLine = message.Split('\n', 2)[0].Trim();
        return new(
            DockerError.CommandFailed,
            string.IsNullOrEmpty(firstLine)
                ? $"Docker exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}."
                : firstLine);
    }

    private static string ComposeUpCommand(DockerOperation.ComposeUp up)
    {
        if (up.ConfigFiles.Count == 0)
        {
            throw new ArgumentException("Compose needs at least one file to bring a project up.", nameof(up));
        }

        var command = new StringBuilder("docker compose");
        if (up.Project is not null)
        {
            _ = command.Append(" -p ").Append(QuoteProject(up.Project));
        }

        foreach (var file in up.ConfigFiles)
        {
            _ = command.Append(" -f ").Append(IsValidComposeFile(file)
                ? Quote(file)
                : throw new ArgumentException($"'{file}' is not a usable compose file path.", nameof(up)));
        }

        return command.Append(" up --detach").ToString();
    }

    private static string QuoteProject(string project)
    {
        return IsValidProjectName(project)
            ? Quote(project)
            : throw new ArgumentException($"'{project}' is not a valid compose project name.", nameof(project));
    }

    private static IReadOnlyList<string> SplitList(string? value)
    {
        return string.IsNullOrEmpty(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Docker writes "&lt;none&gt;" for an untagged image's repository and tag.</summary>
    private static string? NoneAsNull(string? value)
    {
        return value is null or "<none>" ? null : value;
    }

    private static string QuoteReference(string container)
    {
        return IsValidContainerReference(container)
            ? Quote(container)
            : throw new ArgumentException($"'{container}' is not a valid container name or ID.", nameof(container));
    }

    private static IEnumerable<string> JsonLines(string output)
    {
        return output
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line[0] == '{');
    }

    private static string Text(JsonElement row, string property)
    {
        return OptionalText(row, property) ?? string.Empty;
    }

    private static string? OptionalText(JsonElement row, string property)
    {
        return row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? NullIfEmpty(value.GetString())
            : null;
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static DockerContainerState ParseState(string state)
    {
        return Enum.TryParse<DockerContainerState>(state, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            && !int.TryParse(state, out _)
            ? parsed
            : DockerContainerState.Unknown;
    }

    private static double? ParsePercent(string value)
    {
        return double.TryParse(
            value.TrimEnd('%').Trim(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    // \z, not $: in .NET, $ also matches just before a trailing newline, which would let "web\n" through.
    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerReference();

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.:/@-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImageReference();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectName();
}
