using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Validation;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>Folder and tag edits change data that lives in a backup archive, so automatic backup depends
/// on them being announced. These tests pin both halves of that: that a real change signals, and — just as
/// important — that a rolled-back or no-op change does not.</summary>
public sealed class WorkspaceChangeNotificationTests
{
    [Fact]
    public async Task CreateRenameMoveAndDeleteEachSignalExactlyOnce()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await WorkspaceFixture.CreateAsync(token);

        var root = (await fixture.Folders.CreateAsync("Prod", cancellationToken: token)).Value;
        Assert.Equal([(WorkspaceEntityKind.Folder, WorkspaceChangeKind.Created)], fixture.Kinds);

        fixture.Clear();
        var child = (await fixture.Folders.CreateAsync("EU", root.Id, token)).Value;
        _ = await fixture.Folders.RenameAsync(root.Id, "Production", token);
        _ = await fixture.Folders.MoveAsync(child.Id, null, token);
        _ = await fixture.Folders.DeleteAsync(child.Id, cancellationToken: token);

        Assert.Equal(
            [
                (WorkspaceEntityKind.Folder, WorkspaceChangeKind.Created),
                (WorkspaceEntityKind.Folder, WorkspaceChangeKind.Updated),
                (WorkspaceEntityKind.Folder, WorkspaceChangeKind.Updated),
                (WorkspaceEntityKind.Folder, WorkspaceChangeKind.Deleted),
            ],
            fixture.Kinds);
    }

    [Fact]
    public async Task AFailedFolderOperationSignalsNothing()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await WorkspaceFixture.CreateAsync(token);
        var left = (await fixture.Folders.CreateAsync("Left", cancellationToken: token)).Value;
        var moving = (await fixture.Folders.CreateAsync("Database", left.Id, token)).Value;
        fixture.Clear();

        var cycle = await fixture.Folders.MoveAsync(left.Id, moving.Id, token);
        var missing = await fixture.Folders.RenameAsync(Guid.NewGuid(), "Nope", token);
        var badMode = await fixture.Folders.DeleteAsync(left.Id, (FolderDeleteMode)99, token);

        Assert.True(cycle.IsFailure);
        Assert.True(missing.IsFailure);
        Assert.True(badMode.IsFailure);
        Assert.Empty(fixture.Events);
    }

    /// <summary>The one genuinely dangerous path. Deleting a subtree along with its connections calls
    /// ConnectionService inside the ambient transaction, so connection signals fire before commit. A
    /// subscriber that reads the store must still end up seeing committed state, which it does because the
    /// folder signal — the last one — is raised after the unit of work returns.</summary>
    [Fact]
    public async Task DeletingASubtreeWithItsConnectionsSignalsTheFolderOnlyAfterTheTransactionCommits()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await WorkspaceFixture.CreateAsync(token);
        var folder = (await fixture.Folders.CreateAsync("Doomed", cancellationToken: token)).Value;
        var connection = (await fixture.Connections.CreateAsync(
            new ConnectionInput("Host", "host.test", 22, ProtocolType.Ssh, "root"), token)).Value;
        _ = await fixture.Connections.MoveToFolderAsync(connection.Id, folder.Id, token);
        fixture.Clear();

        var observed = new List<int>();
        fixture.Notifier.WorkspaceChanged += (_, _) =>
            observed.Add(fixture.ConnectionRepository.ListAsync(token).GetAwaiter().GetResult().Count);

        var deleted = await fixture.Folders.DeleteAsync(
            folder.Id, FolderDeleteMode.DeleteSubtreeAndConnections, token);

        Assert.True(deleted.IsSuccess);
        Assert.Equal([(WorkspaceEntityKind.Folder, WorkspaceChangeKind.Deleted)], fixture.Kinds);
        Assert.Equal([0], observed);
    }

    [Fact]
    public async Task TagAssignAndUnassignBothSignal()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await WorkspaceFixture.CreateAsync(token);
        var tag = (await fixture.Tags.CreateAsync("prod", cancellationToken: token)).Value;
        var connection = (await fixture.Connections.CreateAsync(
            new ConnectionInput("Host", "host.test", 22, ProtocolType.Ssh, "root"), token)).Value;
        fixture.Clear();

        _ = await fixture.Tags.AssignAsync(connection.Id, tag.Id, token);
        _ = await fixture.Tags.UnassignAsync(connection.Id, tag.Id, token);

        Assert.Equal(
            [
                (WorkspaceEntityKind.Tag, WorkspaceChangeKind.Updated),
                (WorkspaceEntityKind.Tag, WorkspaceChangeKind.Updated),
            ],
            fixture.Kinds);
        Assert.All(fixture.Events, observed => Assert.Equal(tag.Id, observed.EntityId));
    }

    /// <summary>Creating a tag whose name is taken returns the existing one, having written nothing. A
    /// signal there would wake the backup runner for a change that never happened.</summary>
    [Fact]
    public async Task CreatingATagThatAlreadyExistsDoesNotSignal()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await WorkspaceFixture.CreateAsync(token);
        var first = await fixture.Tags.CreateAsync("prod", cancellationToken: token);
        fixture.Clear();

        var second = await fixture.Tags.CreateAsync("prod", cancellationToken: token);

        Assert.True(second.IsSuccess);
        Assert.Equal(first.Value.Id, second.Value.Id);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task RenamingAndDeletingATagSignal()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await WorkspaceFixture.CreateAsync(token);
        var tag = (await fixture.Tags.CreateAsync("prod", cancellationToken: token)).Value;
        fixture.Clear();

        _ = await fixture.Tags.RenameAsync(tag.Id, "production", token);
        _ = await fixture.Tags.DeleteAsync(tag.Id, token);

        Assert.Equal(
            [
                (WorkspaceEntityKind.Tag, WorkspaceChangeKind.Updated),
                (WorkspaceEntityKind.Tag, WorkspaceChangeKind.Deleted),
            ],
            fixture.Kinds);
    }

    [Fact]
    public async Task CleanupOrphansSignalsOnlyWhenSomethingWasDeleted()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await WorkspaceFixture.CreateAsync(token);
        _ = await fixture.Tags.CreateAsync("unused", cancellationToken: token);
        fixture.Clear();

        var first = await fixture.Tags.CleanupOrphansAsync(token);
        var firstEvents = fixture.Events.Count;
        fixture.Clear();
        var second = await fixture.Tags.CleanupOrphansAsync(token);

        Assert.Equal(1, first);
        Assert.Equal(1, firstEvents);
        Assert.Equal(0, second);
        Assert.Empty(fixture.Events);
    }

    private sealed class WorkspaceFixture : IAsyncDisposable
    {
        private readonly SqliteTempDbFixture _database;

        private WorkspaceFixture(SqliteTempDbFixture database)
        {
            _database = database;
            var guids = SystemGuidProvider.Instance;
            var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));
            ConnectionRepository = new ConnectionRepository(database.Factory);
            var folderRepository = new FolderRepository(database.Factory);
            var tagRepository = new TagRepository(database.Factory);
            var unitOfWork = new UnitOfWork(database.Factory);
            Connections = new ConnectionService(
                ConnectionRepository,
                new RecentConnectionStore(database.Factory),
                [],
                unitOfWork,
                guids,
                clock);
            Folders = new FolderService(
                folderRepository, ConnectionRepository, Connections, unitOfWork, guids, clock, Notifier);
            Tags = new TagService(
                tagRepository, ConnectionRepository, unitOfWork, guids, clock, Notifier);
            Notifier.WorkspaceChanged += (_, args) => Events.Add(args);
        }

        public WorkspaceChangeNotifier Notifier { get; } = new();

        public List<WorkspaceChangedEventArgs> Events { get; } = [];

        public IEnumerable<(WorkspaceEntityKind, WorkspaceChangeKind)> Kinds =>
            Events.Select(observed => (observed.Entity, observed.Kind));

        public ConnectionRepository ConnectionRepository { get; }

        public ConnectionService Connections { get; }

        public FolderService Folders { get; }

        public TagService Tags { get; }

        public static async Task<WorkspaceFixture> CreateAsync(CancellationToken cancellationToken)
        {
            return new WorkspaceFixture(await SqliteTempDbFixture.CreateAsync(cancellationToken));
        }

        public void Clear()
        {
            Events.Clear();
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }
}
