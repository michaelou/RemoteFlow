using System.Collections.ObjectModel;
using System.Text.Json;

namespace RemoteFlow.Application.Services;

/// <summary>What running a command unedited can cost, so the palette can warn before the Enter that runs it.</summary>
public enum CommandRisk
{
    Safe = 0,
    Warning = 1,
    Danger = 2,
}

/// <summary>One command in the library, carrying the group it was read from so a result can say where it
/// came from without the caller holding the group as well.</summary>
public sealed record CommandSnippet(
    string Id,
    string GroupId,
    string GroupName,
    string Title,
    string Command,
    string Description,
    IReadOnlyList<string> Tags,
    CommandRisk Risk)
{
    /// <summary>True when the command has a <c>&lt;placeholder&gt;</c> to fill in before it can be run.
    /// Inserting is not running, which is what makes a command with a hole in it worth offering at all.</summary>
    public bool HasPlaceholder => FindPlaceholder(Command);

    private static bool FindPlaceholder(string command)
    {
        // A bare '>' is a redirection — `2>/dev/null` is in the library twice — so the opening angle
        // bracket has to be there too, with a single word between the pair.
        var open = command.IndexOf('<', StringComparison.Ordinal);
        if (open < 0)
        {
            return false;
        }

        var close = command.IndexOf('>', open + 1);
        return close > open + 1 &&
            !command.AsSpan(open + 1, close - open - 1).ContainsAny(' ', '\t');
    }
}

public sealed record CommandSnippetGroup(string Id, string Name, IReadOnlyList<CommandSnippet> Commands);

/// <summary>
/// The commands offered at a prompt, grouped as they are written down and searchable as one flat list.
/// </summary>
/// <remarks>
/// The catalog is embedded rather than read from disk so a portable zip carries it, the same reasoning as
/// the third-party notices. A user-editable library is a later step; when it arrives it supplies the
/// groups through the collection constructor and nothing else here has to change.
/// </remarks>
public sealed class CommandSnippetLibrary
{
    private const string _resourceName = "RemoteFlow.Application.command-snippets.json";

    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Reads the built-in catalog. Never throws: a terminal is where someone goes when something
    /// is already wrong, and a library that cannot be read must not take the page down with it.</summary>
    public CommandSnippetLibrary()
    {
        try
        {
            Groups = ReadEmbedded();
        }
        catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException)
        {
            Groups = [];
            LoadError = $"The command library could not be read from this build: {exception.Message}";
        }

        Commands = Flatten(Groups);
    }

    public CommandSnippetLibrary(IReadOnlyList<CommandSnippetGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        Groups = groups;
        Commands = Flatten(groups);
    }

    /// <summary>The groups in the order they are written down; the order the palette shows them in.</summary>
    public IReadOnlyList<CommandSnippetGroup> Groups { get; }

    /// <summary>Every command, flattened, in catalog order.</summary>
    public IReadOnlyList<CommandSnippet> Commands { get; }

    /// <summary>Why the library is empty, when it is empty because reading it failed.</summary>
    public string? LoadError { get; }

    /// <summary>Parses a catalog document. Throws on malformed JSON, which is what a test wants and what
    /// the embedded constructor turns into <see cref="LoadError" />.</summary>
    public static CommandSnippetLibrary FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return new CommandSnippetLibrary(Parse(json));
    }

    /// <summary>
    /// The commands matching every term in <paramref name="text" />, best match first.
    /// </summary>
    /// <remarks>
    /// Every term has to match somewhere, so typing more words narrows rather than widens — "docker logs"
    /// finds the log commands and not every container command. Matching is in memory and takes
    /// microseconds over a catalog this size, so unlike the connection palette there is nothing here to
    /// debounce.
    /// </remarks>
    public IReadOnlyList<CommandSnippet> Search(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Commands;
        }

        var terms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = new List<(CommandSnippet Snippet, int Score, int Order)>();
        for (var order = 0; order < Commands.Count; order++)
        {
            var snippet = Commands[order];
            var total = 0;
            foreach (var term in terms)
            {
                var score = Score(snippet, term);
                if (score == 0)
                {
                    total = 0;
                    break;
                }

                total += score;
            }

            if (total > 0)
            {
                matches.Add((snippet, total, order));
            }
        }

        return
        [
            .. matches
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.Order)
                .Select(match => match.Snippet),
        ];
    }

    /// <summary>
    /// How well one term matches one command. Zero excludes the command outright.
    /// </summary>
    /// <remarks>
    /// A ranked table rather than a single relevance number: where the term was found is the whole of what
    /// decides the order, and the order these are written in is the order a reader expects to be offered.
    /// </remarks>
    private static int Score(CommandSnippet snippet, string term)
    {
        return snippet switch
        {
            _ when snippet.Title.StartsWith(term, StringComparison.OrdinalIgnoreCase) => 100,
            _ when snippet.Command.StartsWith(term, StringComparison.OrdinalIgnoreCase) => 90,
            // A word inside the title, so "usage" finds "Check disk usage" ahead of anything that only
            // mentions usage in its description.
            _ when StartsAWord(snippet.Title, term) => 80,
            _ when snippet.Tags.Any(tag => tag.Equals(term, StringComparison.OrdinalIgnoreCase)) => 70,
            _ when snippet.Command.Contains(term, StringComparison.OrdinalIgnoreCase) => 50,
            _ when snippet.Title.Contains(term, StringComparison.OrdinalIgnoreCase) => 40,
            _ when snippet.Tags.Any(tag => tag.StartsWith(term, StringComparison.OrdinalIgnoreCase)) => 30,
            _ when snippet.GroupName.Contains(term, StringComparison.OrdinalIgnoreCase) => 20,
            _ when snippet.Description.Contains(term, StringComparison.OrdinalIgnoreCase) => 10,
            _ => 0,
        };
    }

    private static bool StartsAWord(string text, string term)
    {
        for (var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
             index > 0;
             index = text.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (!char.IsLetterOrDigit(text[index - 1]))
            {
                return true;
            }
        }

        return false;
    }

    private static ReadOnlyCollection<CommandSnippet> Flatten(IReadOnlyList<CommandSnippetGroup> groups)
    {
        return groups.SelectMany(group => group.Commands).ToArray().AsReadOnly();
    }

    private static ReadOnlyCollection<CommandSnippetGroup> ReadEmbedded()
    {
        using var stream = typeof(CommandSnippetLibrary).Assembly.GetManifestResourceStream(_resourceName)
            ?? throw new IOException($"The resource {_resourceName} is not embedded in this build.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    private static ReadOnlyCollection<CommandSnippetGroup> Parse(string json)
    {
        var document = JsonSerializer.Deserialize<CatalogDocument>(json, _serializerOptions);
        if (document?.Groups is null)
        {
            return [];
        }

        var groups = new List<CommandSnippetGroup>(document.Groups.Count);
        foreach (var group in document.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id) || group.Commands is null)
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(group.Name) ? group.Id : group.Name;
            var commands = group.Commands
                .Where(command => !string.IsNullOrWhiteSpace(command.Id) && !string.IsNullOrWhiteSpace(command.Command))
                .Select(command => new CommandSnippet(
                    command.Id!,
                    group.Id!,
                    name!,
                    string.IsNullOrWhiteSpace(command.Title) ? command.Command! : command.Title!,
                    command.Command!,
                    command.Description ?? string.Empty,
                    command.Tags ?? [],
                    ParseRisk(command.Risk)))
                .ToArray();
            if (commands.Length > 0)
            {
                groups.Add(new CommandSnippetGroup(group.Id!, name!, commands));
            }
        }

        return groups.AsReadOnly();
    }

    /// <summary>An unrecognised risk reads as the most dangerous rather than the safest: a typo in the
    /// catalog must not be what removes a warning.</summary>
    private static CommandRisk ParseRisk(string? risk)
    {
        return risk?.Trim().ToLowerInvariant() switch
        {
            "safe" => CommandRisk.Safe,
            "warning" => CommandRisk.Warning,
            null or "" => CommandRisk.Safe,
            _ => CommandRisk.Danger,
        };
    }

    private sealed record CatalogDocument(IReadOnlyList<CatalogGroup>? Groups);

    private sealed record CatalogGroup(string? Id, string? Name, IReadOnlyList<CatalogCommand>? Commands);

    private sealed record CatalogCommand(
        string? Id,
        string? Title,
        string? Command,
        string? Description,
        IReadOnlyList<string>? Tags,
        string? Risk);
}
