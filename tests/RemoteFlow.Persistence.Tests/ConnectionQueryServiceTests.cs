using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Queries;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Persistence.Queries;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Persistence.Tests;

public sealed class ConnectionQueryServiceTests
{
    [Fact]
    public async Task TextSearchSpansNameHostUsernameNotesAndTagNames()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await QueryFixture.CreateAsync(token);
        _ = await fixture.AddConnectionAsync("needle name", "one.test", cancellationToken: token);
        _ = await fixture.AddConnectionAsync("Host", "needle.test", cancellationToken: token);
        _ = await fixture.AddConnectionAsync("User", "three.test", username: "needle-user", cancellationToken: token);
        _ = await fixture.AddConnectionAsync("Notes", "four.test", notes: "contains needle here", cancellationToken: token);
        var tagged = await fixture.AddConnectionAsync("Tagged", "five.test", cancellationToken: token);
        var tag = await fixture.AddTagAsync("needle-tag", token);
        _ = await fixture.Connections.AddTagAsync(tagged.Id, tag.Id, token);

        var results = await fixture.Service.QueryAsync(new ConnectionFilter { Text = "NEEDLE" }, token);

        Assert.Equal(5, results.Count);
        Assert.Contains(results, item => item.TagNames.Contains("needle-tag"));
    }

    [Fact]
    public async Task TagAndOrFiltersHaveTheExpectedSetSemantics()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await QueryFixture.CreateAsync(token);
        var blue = await fixture.AddTagAsync("Blue", token);
        var green = await fixture.AddTagAsync("Green", token);
        var both = await fixture.AddConnectionAsync("Both", "both.test", cancellationToken: token);
        var one = await fixture.AddConnectionAsync("One", "one.test", cancellationToken: token);
        _ = await fixture.AddConnectionAsync("Neither", "neither.test", cancellationToken: token);
        _ = await fixture.Connections.AddTagAsync(both.Id, blue.Id, token);
        _ = await fixture.Connections.AddTagAsync(both.Id, green.Id, token);
        _ = await fixture.Connections.AddTagAsync(one.Id, blue.Id, token);

        var andResults = await fixture.Service.QueryAsync(new ConnectionFilter
        {
            Tags = [blue.Id, green.Id],
            TagMatch = TagMatch.And,
        }, token);
        var orResults = await fixture.Service.QueryAsync(new ConnectionFilter
        {
            Tags = [blue.Id, green.Id],
            TagMatch = TagMatch.Or,
        }, token);

        Assert.Equal("Both", Assert.Single(andResults).Name);
        Assert.Equal(["Both", "One"], [.. orResults.Select(item => item.Name)]);
    }

    [Fact]
    public async Task FiltersComposeAndAnEmptyFilterReturnsDefaultNameOrder()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await QueryFixture.CreateAsync(token);
        var root = await fixture.AddFolderAsync("Prod", cancellationToken: token);
        var child = await fixture.AddFolderAsync("EU", root, token);
        var tag = await fixture.AddTagAsync("Critical", token);
        var match = await fixture.AddConnectionAsync(
            "Zulu match",
            "match.test",
            ProtocolType.Ssh,
            EnvironmentKind.Production,
            child.Id,
            cancellationToken: token);
        _ = await fixture.Connections.AddTagAsync(match.Id, tag.Id, token);
        _ = await fixture.AddConnectionAsync("Alpha", "alpha.test", cancellationToken: token);
        _ = await fixture.AddConnectionAsync(
            "Wrong protocol",
            "rdp.test",
            ProtocolType.Rdp,
            EnvironmentKind.Production,
            child.Id,
            cancellationToken: token);

        var composed = await fixture.Service.QueryAsync(new ConnectionFilter
        {
            Protocols = [ProtocolType.Ssh],
            Environments = [EnvironmentKind.Production],
            Tags = [tag.Id],
            FolderId = root.Id,
            IncludeDescendants = true,
        }, token);
        var all = await fixture.Service.QueryAsync(new ConnectionFilter(), token);

        Assert.Equal(match.Id, Assert.Single(composed).Id);
        Assert.Equal(["Alpha", "Wrong protocol", "Zulu match"], [.. all.Select(item => item.Name)]);
        Assert.Equal("/Prod/EU", composed[0].FolderPath);
    }

    [Fact]
    public async Task PaletteRanksPrefixAboveSubstringAboveFuzzyAndBoostsRecencyWithinATier()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await QueryFixture.CreateAsync(token);
        var stalePrefix = await fixture.AddConnectionAsync("Prod stale", "stale.test", cancellationToken: token);
        var recentPrefix = await fixture.AddConnectionAsync("Prod recent", "recent.test", cancellationToken: token);
        _ = await fixture.AddConnectionAsync("My prod server", "substring.test", cancellationToken: token);
        _ = await fixture.AddConnectionAsync("P-r-o-d", "fuzzy.test", cancellationToken: token);
        await fixture.Recent.RecordOpenedAsync(stalePrefix.Id, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), token);
        await fixture.Recent.RecordOpenedAsync(recentPrefix.Id, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), token);

        var results = await fixture.Service.SearchPaletteAsync("prod", cancellationToken: token);

        Assert.Equal(["Prod recent", "Prod stale", "My prod server", "P-r-o-d"], [.. results.Select(item => item.Name)]);
    }

    [Fact]
    public async Task ProjectionSqlDoesNotSelectOwnedOptionOrNavigationColumns()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await QueryFixture.CreateAsync(token);
        _ = await fixture.AddConnectionAsync("Server", "server.test", cancellationToken: token);
        var messages = new List<string>();
        var options = new DbContextOptionsBuilder<RemoteFlowDbContext>()
            .UseSqlite($"Data Source={fixture.Database.DatabasePath}")
            .LogTo(messages.Add)
            .Options;
        var service = new ConnectionQueryService(new TestContextFactory(options));

        _ = await service.QueryAsync(new ConnectionFilter(), token);
        var projectionCommand = Assert.Single(messages, message =>
            message.Contains("Executed DbCommand", StringComparison.Ordinal) &&
            message.Contains("FROM \"Connections\" AS", StringComparison.Ordinal) &&
            message.Contains("SELECT", StringComparison.Ordinal));

        Assert.DoesNotContain("Credential_", projectionCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("Ssh_", projectionCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("Sftp_", projectionCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("Rdp_", projectionCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionTags", projectionCommand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubtreePathLookupUsesThePathIndex()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await QueryFixture.CreateAsync(token);
        var root = await fixture.AddFolderAsync("Prod", cancellationToken: token);
        _ = await fixture.AddFolderAsync("EU", root, token);
        await using var context = await fixture.Database.Factory.CreateDbContextAsync(token);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT \"Id\" FROM \"Folders\" WHERE \"Path\" LIKE '/Prod/%' ESCAPE '\\'";
        var details = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            details.Add(reader.GetString(3));
        }

        Assert.Contains(details, detail => detail.Contains("IX_Folders_Path", StringComparison.Ordinal));
    }

    private sealed class QueryFixture : IAsyncDisposable
    {
        private static readonly DateTimeOffset _now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);
        private readonly IGuidProvider _guids = SystemGuidProvider.Instance;

        private QueryFixture(SqliteTempDbFixture database)
        {
            Database = database;
            Connections = new ConnectionRepository(database.Factory);
            Folders = new FolderRepository(database.Factory);
            Tags = new TagRepository(database.Factory);
            Recent = new RecentConnectionStore(database.Factory);
            Service = new ConnectionQueryService(database.Factory);
        }

        public SqliteTempDbFixture Database { get; }

        public ConnectionRepository Connections { get; }

        public FolderRepository Folders { get; }

        public TagRepository Tags { get; }

        public RecentConnectionStore Recent { get; }

        public ConnectionQueryService Service { get; }

        public static async Task<QueryFixture> CreateAsync(CancellationToken cancellationToken)
        {
            return new QueryFixture(await SqliteTempDbFixture.CreateAsync(cancellationToken));
        }

        public async Task<Folder> AddFolderAsync(
            string name,
            Folder? parent = null,
            CancellationToken cancellationToken = default)
        {
            var folder = Folder.Create(_guids, name, parent, createdUtc: _now).Value;
            await Folders.AddAsync(folder, cancellationToken);
            return folder;
        }

        public async Task<Tag> AddTagAsync(string name, CancellationToken cancellationToken)
        {
            var tag = Tag.Create(_guids, name, createdUtc: _now).Value;
            await Tags.AddAsync(tag, cancellationToken);
            return tag;
        }

        public async Task<Connection> AddConnectionAsync(
            string name,
            string host,
            ProtocolType protocol = ProtocolType.Ssh,
            EnvironmentKind environment = EnvironmentKind.Unspecified,
            Guid? folderId = null,
            string? username = null,
            string? notes = null,
            CancellationToken cancellationToken = default)
        {
            var connection = Connection.Create(_guids, name, host, protocol, _now).Value;
            _ = connection.SetDetails(
                username,
                AuthMethod.None,
                notes,
                environment,
                null,
                _guids,
                _now);
            _ = connection.SetFolder(folderId, _guids, _now);
            await Connections.AddAsync(connection, cancellationToken);
            return connection;
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class TestContextFactory(DbContextOptions<RemoteFlowDbContext> options)
        : IDbContextFactory<RemoteFlowDbContext>
    {
        public RemoteFlowDbContext CreateDbContext()
        {
            return new RemoteFlowDbContext(options);
        }

        public Task<RemoteFlowDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
