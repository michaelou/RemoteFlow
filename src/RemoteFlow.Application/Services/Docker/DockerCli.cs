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
        "\"service\":{{json (.Label \"com.docker.compose.service\")}}}";

    /// <summary>The command that replaces the login shell of a fresh terminal with a shell inside the
    /// container: bash when the image has it, sh when it does not. <c>exec</c>, so leaving the container
    /// ends the tab instead of dropping back to the host without a word.</summary>
    private const string _shellProbe = "command -v bash >/dev/null 2>&1 && exec bash || exec sh";

    public static string ListContainersCommand { get; } = $"docker ps --all --format {Quote(_listFormat)}";

    public static string StatsCommand { get; } = "docker stats --no-stream --format '{{json .}}'";

    public static string VersionCommand { get; } = "docker version --format '{{.Server.Version}}'";

    /// <summary>Docker's own grammar for a container name, which also admits every ID, full or short.</summary>
    public static bool IsValidContainerReference(string? reference)
    {
        return !string.IsNullOrEmpty(reference) && reference.Length <= 255 && ContainerReference().IsMatch(reference);
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
                OptionalText(row, "service")));
        }

        return containers;
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

        if (message.Contains("Cannot connect to the Docker daemon", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Is the docker daemon running", StringComparison.OrdinalIgnoreCase))
        {
            return new(DockerError.DaemonUnavailable, "Docker is installed but its daemon is not running on this server.");
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
}
