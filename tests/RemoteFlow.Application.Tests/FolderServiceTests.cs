using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class FolderServiceTests
{
    [Fact]
    public async Task Rename_RewritesAThreeLevelSubtreeInOneTransaction()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await FolderFixture.CreateAsync(token);
        var root = (await fixture.Service.CreateAsync("Prod", cancellationToken: token)).Value;
        var child = (await fixture.Service.CreateAsync("EU", root.Id, token)).Value;
        var grandchild = (await fixture.Service.CreateAsync("Database", child.Id, token)).Value;

        var result = await fixture.Service.RenameAsync(root.Id, "Production", token);
        var folders = await fixture.Folders.ListAsync(token);

        Assert.True(result.IsSuccess);
        Assert.Equal("/Production", Find(folders, root.Id).Path);
        Assert.Equal("/Production/EU", Find(folders, child.Id).Path);
        Assert.Equal("/Production/EU/Database", Find(folders, grandchild.Id).Path);
        AssertConsistent(folders);
    }

    [Fact]
    public async Task Move_RejectsCycleAndSiblingCollisionWithoutChangingTree()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await FolderFixture.CreateAsync(token);
        var left = (await fixture.Service.CreateAsync("Left", cancellationToken: token)).Value;
        var right = (await fixture.Service.CreateAsync("Right", cancellationToken: token)).Value;
        var moving = (await fixture.Service.CreateAsync("Database", left.Id, token)).Value;
        var descendant = (await fixture.Service.CreateAsync("Child", moving.Id, token)).Value;
        _ = await fixture.Service.CreateAsync("Database", right.Id, token);
        var before = Snapshot(await fixture.Folders.ListAsync(token));

        var cycle = await fixture.Service.MoveAsync(moving.Id, descendant.Id, token);
        var collision = await fixture.Service.MoveAsync(moving.Id, right.Id, token);
        var after = Snapshot(await fixture.Folders.ListAsync(token));

        Assert.Equal("folder.cycle", cycle.Error.Code);
        Assert.Equal("folder.name_collision", collision.Error.Code);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task DeleteWithDefaultMode_ReparentsChildrenAndConnections()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await FolderFixture.CreateAsync(token);
        var root = (await fixture.Service.CreateAsync("Root", cancellationToken: token)).Value;
        var removed = (await fixture.Service.CreateAsync("Removed", root.Id, token)).Value;
        var child = (await fixture.Service.CreateAsync("Child", removed.Id, token)).Value;
        var connection = await fixture.AddConnectionAsync("Server", removed.Id, token);

        var result = await fixture.Service.DeleteAsync(removed.Id, cancellationToken: token);
        var folders = await fixture.Folders.ListAsync(token);
        var persistedConnection = await fixture.Connections.GetByIdAsync(connection.Id, token);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(folders, folder => folder.Id == removed.Id);
        Assert.Equal(root.Id, Find(folders, child.Id).ParentId);
        Assert.Equal("/Root/Child", Find(folders, child.Id).Path);
        Assert.Equal(root.Id, persistedConnection!.FolderId);
        AssertConsistent(folders);
    }

    [Fact]
    public async Task DeleteSubtree_ReparentsConnectionsAndRemovesEveryFolder()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await FolderFixture.CreateAsync(token);
        var root = (await fixture.Service.CreateAsync("Root", cancellationToken: token)).Value;
        var removed = (await fixture.Service.CreateAsync("Removed", root.Id, token)).Value;
        var child = (await fixture.Service.CreateAsync("Child", removed.Id, token)).Value;
        var connection = await fixture.AddConnectionAsync("Server", child.Id, token);

        var result = await fixture.Service.DeleteAsync(removed.Id, FolderDeleteMode.DeleteSubtree, token);
        var folders = await fixture.Folders.ListAsync(token);
        var persistedConnection = await fixture.Connections.GetByIdAsync(connection.Id, token);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(folders, folder => folder.Id == removed.Id || folder.Id == child.Id);
        Assert.Equal(root.Id, persistedConnection!.FolderId);
        AssertConsistent(folders);
    }

    [Fact]
    public async Task Create_RejectsDepthSeventeenWithClearError()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await FolderFixture.CreateAsync(token);
        Guid? parentId = null;
        for (var depth = 0; depth <= FolderService.MaximumDepth; depth++)
        {
            parentId = (await fixture.Service.CreateAsync($"Level-{depth}", parentId, token)).Value.Id;
        }

        var result = await fixture.Service.CreateAsync("Too deep", parentId, token);

        Assert.True(result.IsFailure);
        Assert.Equal("folder.depth_limit", result.Error.Code);
        Assert.Contains("16", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FaultDuringSubtreeUpdate_RollsBackTheWholeMove()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await FolderFixture.CreateAsync(token);
        var source = (await fixture.Service.CreateAsync("Source", cancellationToken: token)).Value;
        var target = (await fixture.Service.CreateAsync("Target", cancellationToken: token)).Value;
        var child = (await fixture.Service.CreateAsync("Child", source.Id, token)).Value;
        _ = await fixture.Service.CreateAsync("Grandchild", child.Id, token);
        var before = Snapshot(await fixture.Folders.ListAsync(token));
        var faultingService = fixture.CreateService(new FaultingFolderRepository(fixture.Folders, failOnUpdate: 2));

        _ = await Assert.ThrowsAsync<InjectedFolderFault>(() =>
            faultingService.MoveAsync(child.Id, target.Id, token));
        var after = Snapshot(await fixture.Folders.ListAsync(token));

        Assert.Equal(before, after);
    }

    private static Folder Find(IEnumerable<Folder> folders, Guid id)
    {
        return folders.Single(folder => folder.Id == id);
    }

    private static string[] Snapshot(IEnumerable<Folder> folders)
    {
        return [.. folders.OrderBy(folder => folder.Id).Select(folder =>
            $"{folder.Id}|{folder.ParentId}|{folder.Path}|{folder.Depth}")];
    }

    private static void AssertConsistent(IReadOnlyCollection<Folder> folders)
    {
        foreach (var folder in folders)
        {
            var parent = folder.ParentId is null
                ? null
                : folders.Single(candidate => candidate.Id == folder.ParentId.Value);
            Assert.Equal(parent is null ? $"/{folder.Name}" : $"{parent.Path}/{folder.Name}", folder.Path);
            Assert.Equal(parent is null ? 0 : parent.Depth + 1, folder.Depth);
        }
    }

    private sealed class FolderFixture : IAsyncDisposable
    {
        private readonly SqliteTempDbFixture _database;
        private readonly IGuidProvider _guids = SystemGuidProvider.Instance;
        private readonly FakeClock _clock = new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        private readonly IUnitOfWork _unitOfWork;
        private readonly IConnectionService _connectionService;

        private FolderFixture(SqliteTempDbFixture database)
        {
            _database = database;
            Folders = new FolderRepository(database.Factory);
            Connections = new ConnectionRepository(database.Factory);
            var recent = new RecentConnectionStore(database.Factory);
            _unitOfWork = new UnitOfWork(database.Factory);
            _connectionService = new ConnectionService(
                Connections,
                recent,
                [],
                _unitOfWork,
                _guids,
                _clock);
            Service = CreateService(Folders);
        }

        public FolderRepository Folders { get; }

        public ConnectionRepository Connections { get; }

        public FolderService Service { get; }

        public static async Task<FolderFixture> CreateAsync(CancellationToken cancellationToken)
        {
            return new FolderFixture(await SqliteTempDbFixture.CreateAsync(cancellationToken));
        }

        public FolderService CreateService(IFolderRepository folderRepository)
        {
            return new FolderService(
                folderRepository,
                Connections,
                _connectionService,
                _unitOfWork,
                _guids,
                _clock);
        }

        public async Task<Connection> AddConnectionAsync(
            string name,
            Guid folderId,
            CancellationToken cancellationToken)
        {
            var connection = new ConnectionBuilder()
                .WithName(name)
                .WithGuidProvider(_guids)
                .Build()
                .SetFolder(folderId, _guids, _clock.UtcNow);
            await Connections.AddAsync(connection, cancellationToken);
            return connection;
        }

        public ValueTask DisposeAsync()
        {
            return _database.DisposeAsync();
        }
    }

    private sealed class FaultingFolderRepository(IFolderRepository inner, int failOnUpdate) : IFolderRepository
    {
        private int _updates;

        public Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return inner.GetByIdAsync(id, cancellationToken);
        }

        public Task<IReadOnlyList<Folder>> ListAsync(CancellationToken cancellationToken = default)
        {
            return inner.ListAsync(cancellationToken);
        }

        public Task AddAsync(Folder folder, CancellationToken cancellationToken = default)
        {
            return inner.AddAsync(folder, cancellationToken);
        }

        public async Task UpdateAsync(Folder folder, CancellationToken cancellationToken = default)
        {
            await inner.UpdateAsync(folder, cancellationToken);
            _updates++;
            if (_updates == failOnUpdate)
            {
                throw new InjectedFolderFault();
            }
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return inner.DeleteAsync(id, cancellationToken);
        }
    }

    private sealed class InjectedFolderFault : Exception;
}
