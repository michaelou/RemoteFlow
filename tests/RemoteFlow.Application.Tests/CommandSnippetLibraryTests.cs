using RemoteFlow.Application.Services;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class CommandSnippetLibraryTests
{
    private const string _catalog = """
        {
          "groups": [
            {
              "id": "disk",
              "name": "Disk & Space",
              "commands": [
                {
                  "id": "disk-filesystem-usage",
                  "title": "Check disk usage",
                  "command": "df -h",
                  "description": "Show filesystem disk usage and available space.",
                  "tags": ["disk", "space", "filesystem"],
                  "risk": "safe"
                },
                {
                  "id": "disk-large-files",
                  "title": "Find files larger than 1 GB",
                  "command": "find / -type f -size +1G 2>/dev/null",
                  "description": "Search the filesystem for large files.",
                  "tags": ["disk", "space", "find"],
                  "risk": "safe"
                }
              ]
            },
            {
              "id": "docker",
              "name": "Docker & Containers",
              "commands": [
                {
                  "id": "docker-logs",
                  "title": "Container logs",
                  "command": "docker logs <container>",
                  "description": "Display logs from a Docker container.",
                  "tags": ["docker", "container", "logs"],
                  "risk": "safe"
                },
                {
                  "id": "docker-volume-prune",
                  "title": "Prune unused Docker volumes",
                  "command": "docker volume prune",
                  "description": "Remove unused Docker volumes.",
                  "tags": ["docker", "volume", "cleanup"],
                  "risk": "danger"
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void TheBuiltInCatalogShipsInTheAssembly()
    {
        var library = new CommandSnippetLibrary();

        Assert.Null(library.LoadError);
        Assert.NotEmpty(library.Groups);
        Assert.All(library.Commands, snippet =>
        {
            Assert.False(string.IsNullOrWhiteSpace(snippet.Id));
            Assert.False(string.IsNullOrWhiteSpace(snippet.Title));
            Assert.False(string.IsNullOrWhiteSpace(snippet.Command));
            Assert.False(string.IsNullOrWhiteSpace(snippet.GroupName));
        });
        Assert.Equal(
            library.Commands.Select(snippet => snippet.Id).Distinct(StringComparer.Ordinal).Count(),
            library.Commands.Count);
        // The catalog is the reason the feature exists, so an empty or truncated one is a failure and not
        // an empty list to shrug at.
        Assert.Contains(library.Commands, snippet => snippet.Command == "df -h");
        Assert.Contains(library.Commands, snippet => snippet.Risk == CommandRisk.Danger);

        // A disk filled by container logs takes two commands: one finds them, one empties them. The one
        // that empties them destroys the logs, so it has to reach the list marked.
        Assert.Contains(
            library.Search("container log files"),
            snippet => snippet.Id == "docker-large-log-files");
        Assert.Equal(
            CommandRisk.Danger,
            library.Commands.Single(snippet => snippet.Id == "docker-truncate-log-files").Risk);
    }

    [Fact]
    public void AnEmptySearchOffersTheWholeLibraryInCatalogOrder()
    {
        var library = CommandSnippetLibrary.FromJson(_catalog);

        var results = library.Search("   ");

        Assert.Equal(
            ["disk-filesystem-usage", "disk-large-files", "docker-logs", "docker-volume-prune"],
            results.Select(snippet => snippet.Id));
    }

    [Fact]
    public void TitleMatchesOutrankTagAndDescriptionMatches()
    {
        var library = CommandSnippetLibrary.FromJson(_catalog);

        var results = library.Search("logs");

        // "Container logs" has the word in its title; the volume prune command does not mention logs at
        // all, so it must not be offered merely because it is a Docker command.
        Assert.Equal("docker-logs", results[0].Id);
        Assert.DoesNotContain(results, snippet => snippet.Id == "docker-volume-prune");
    }

    [Fact]
    public void EveryTermHasToMatchSoTypingMoreNarrows()
    {
        var library = CommandSnippetLibrary.FromJson(_catalog);

        var oneTerm = library.Search("docker");
        var twoTerms = library.Search("docker prune");

        Assert.Equal(2, oneTerm.Count);
        Assert.Equal("docker-volume-prune", Assert.Single(twoTerms).Id);
    }

    [Fact]
    public void SearchingMatchesTheCommandItselfAndIgnoresCase()
    {
        var library = CommandSnippetLibrary.FromJson(_catalog);

        Assert.Equal("disk-filesystem-usage", Assert.Single(library.Search("DF -H")).Id);
        Assert.Empty(library.Search("kubectl"));
    }

    [Fact]
    public void OnlyCommandsWithSomethingToFillInCountAsHavingAPlaceholder()
    {
        var library = CommandSnippetLibrary.FromJson(_catalog);
        var placeholder = library.Commands.Single(snippet => snippet.Id == "docker-logs");
        // A redirection is not a placeholder: this command is ready to run as written.
        var redirection = library.Commands.Single(snippet => snippet.Id == "disk-large-files");

        Assert.True(placeholder.HasPlaceholder);
        Assert.False(redirection.HasPlaceholder);
    }

    [Fact]
    public void RiskIsReadFromTheCatalogAndAnUnknownRiskIsTreatedAsTheWorstCase()
    {
        var library = CommandSnippetLibrary.FromJson(_catalog.Replace(
            "\"risk\": \"danger\"",
            "\"risk\": \"catastrophic\"",
            StringComparison.Ordinal));

        Assert.Equal(CommandRisk.Safe, library.Commands.Single(snippet => snippet.Id == "docker-logs").Risk);
        Assert.Equal(
            CommandRisk.Danger,
            library.Commands.Single(snippet => snippet.Id == "docker-volume-prune").Risk);
    }

    [Fact]
    public void ACommandWithoutAnIdOrACommandLineIsSkippedRatherThanOffered()
    {
        var library = CommandSnippetLibrary.FromJson("""
            {
              "groups": [
                {
                  "id": "disk",
                  "name": "Disk",
                  "commands": [
                    { "id": "", "title": "Nameless", "command": "df -h" },
                    { "id": "no-command", "title": "Nothing to run" },
                    { "id": "usable", "title": "Memory usage", "command": "free -h" }
                  ]
                }
              ]
            }
            """);

        Assert.Equal("usable", Assert.Single(library.Commands).Id);
    }
}
