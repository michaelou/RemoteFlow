using Microsoft.EntityFrameworkCore;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Persistence.Tests;

public sealed class RepositoryRoundTripTests
{
    [Fact]
    public async Task ConnectionRepositoryRoundTripsAndAddsAndRemovesTags()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        var connections = new ConnectionRepository(database.Factory);
        var tags = new TagRepository(database.Factory);
        var connection = new ConnectionBuilder().Build();
        var tag = new TagBuilder().Build();

        await connections.AddAsync(connection, cancellationToken);
        await tags.AddAsync(tag, cancellationToken);

        Assert.True(await connections.AddTagAsync(connection.Id, tag.Id, cancellationToken));
        Assert.False(await connections.AddTagAsync(connection.Id, tag.Id, cancellationToken));
        var persisted = Assert.IsType<Domain.Entities.Connection>(
            await connections.GetByIdAsync(connection.Id, cancellationToken));
        Assert.Contains(persisted.Tags, item => item.TagId == tag.Id);
        Assert.Equal(1, await tags.GetUsageCountAsync(tag.Id, cancellationToken));

        Assert.True(await connections.RemoveTagAsync(connection.Id, tag.Id, cancellationToken));
        Assert.False(await connections.RemoveTagAsync(connection.Id, tag.Id, cancellationToken));
        persisted = Assert.IsType<Domain.Entities.Connection>(
            await connections.GetByIdAsync(connection.Id, cancellationToken));
        Assert.Empty(persisted.Tags);
    }

    [Fact]
    public async Task FolderTagAndHostKeyRepositoriesRoundTrip()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        var folders = new FolderRepository(database.Factory);
        var tags = new TagRepository(database.Factory);
        var hostKeys = new HostKeyStore(database.Factory);
        var folder = new FolderBuilder().Build();
        var tag = new TagBuilder().Build();
        var hostKey = new HostKeyBuilder().Build();

        await folders.AddAsync(folder, cancellationToken);
        await tags.AddAsync(tag, cancellationToken);
        await hostKeys.AddAsync(hostKey, cancellationToken);

        Assert.Equal(folder.Path, (await folders.GetByIdAsync(folder.Id, cancellationToken))?.Path);
        Assert.Equal(tag.Id, (await tags.GetByNameAsync("production", cancellationToken))?.Id);
        Assert.Equal(
            hostKey.Sha256Fingerprint,
            (await hostKeys.GetAsync(hostKey.Host.ToUpperInvariant(), hostKey.Port, hostKey.KeyAlgorithm, cancellationToken))
                ?.Sha256Fingerprint);
        _ = Assert.Single(await folders.ListAsync(cancellationToken));
        _ = Assert.Single(await tags.ListAsync(cancellationToken));
        _ = Assert.Single(await hostKeys.ListAsync(cancellationToken));
    }

    [Fact]
    public async Task RecentConnectionStoreCreatesUpdatesListsAndRemoves()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        var connections = new ConnectionRepository(database.Factory);
        var recentConnections = new RecentConnectionStore(database.Factory);
        var connection = new ConnectionBuilder().Build();
        var firstOpened = new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

        await connections.AddAsync(connection, cancellationToken);
        await recentConnections.RecordOpenedAsync(connection.Id, firstOpened, cancellationToken);
        await recentConnections.RecordOpenedAsync(connection.Id, firstOpened.AddMinutes(5), cancellationToken);

        var persisted = Assert.IsType<Domain.Entities.RecentConnection>(
            await recentConnections.GetAsync(connection.Id, cancellationToken));
        Assert.Equal(2, persisted.OpenCount);
        Assert.Equal(firstOpened.AddMinutes(5), persisted.LastOpenedUtc);
        _ = Assert.Single(await recentConnections.ListAsync(20, cancellationToken));

        await recentConnections.RemoveAsync(connection.Id, cancellationToken);
        Assert.Null(await recentConnections.GetAsync(connection.Id, cancellationToken));
    }

    [Fact]
    public async Task DeletingConnectionThroughRepositoryCascadesButKeepsTag()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        var connections = new ConnectionRepository(database.Factory);
        var tags = new TagRepository(database.Factory);
        var recentConnections = new RecentConnectionStore(database.Factory);
        var connection = new ConnectionBuilder().Build();
        var tag = new TagBuilder().Build();

        await connections.AddAsync(connection, cancellationToken);
        await tags.AddAsync(tag, cancellationToken);
        _ = await connections.AddTagAsync(connection.Id, tag.Id, cancellationToken);
        await recentConnections.RecordOpenedAsync(connection.Id, DateTimeOffset.UtcNow, cancellationToken);

        await connections.DeleteAsync(connection.Id, cancellationToken);

        Assert.Null(await connections.GetByIdAsync(connection.Id, cancellationToken));
        Assert.Null(await recentConnections.GetAsync(connection.Id, cancellationToken));
        Assert.Equal(0, await tags.GetUsageCountAsync(tag.Id, cancellationToken));
        Assert.NotNull(await tags.GetByIdAsync(tag.Id, cancellationToken));
    }

    [Fact]
    public async Task DeletingTagThroughRepositoryCascadesJoinButKeepsConnection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        var connections = new ConnectionRepository(database.Factory);
        var tags = new TagRepository(database.Factory);
        var connection = new ConnectionBuilder().Build();
        var tag = new TagBuilder().Build();

        await connections.AddAsync(connection, cancellationToken);
        await tags.AddAsync(tag, cancellationToken);
        _ = await connections.AddTagAsync(connection.Id, tag.Id, cancellationToken);
        await tags.DeleteAsync(tag.Id, cancellationToken);

        var persisted = Assert.IsType<Domain.Entities.Connection>(
            await connections.GetByIdAsync(connection.Id, cancellationToken));
        Assert.Empty(persisted.Tags);
    }

    [Fact]
    public async Task DeletingNonEmptyFolderThroughRepositoryIsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        var folders = new FolderRepository(database.Factory);
        var connections = new ConnectionRepository(database.Factory);
        var folder = new FolderBuilder().Build();
        var connection = new ConnectionBuilder().Build();
        _ = connection.SetFolder(folder.Id, SystemGuidProvider.Instance);

        await folders.AddAsync(folder, cancellationToken);
        await connections.AddAsync(connection, cancellationToken);

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => folders.DeleteAsync(folder.Id, cancellationToken));
        Assert.NotNull(await folders.GetByIdAsync(folder.Id, cancellationToken));
    }

    [Fact]
    public async Task UnitOfWorkCommitsAllOperationsAndRollsBackFailures()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await SqliteTempDbFixture.CreateAsync(cancellationToken);
        var connections = new ConnectionRepository(database.Factory);
        var folders = new FolderRepository(database.Factory);
        var tags = new TagRepository(database.Factory);
        var unitOfWork = new UnitOfWork(database.Factory);
        var folder = new FolderBuilder().Build();
        var connection = new ConnectionBuilder().Build();
        _ = connection.SetFolder(folder.Id, SystemGuidProvider.Instance);

        await unitOfWork.ExecuteAsync(async token =>
        {
            await folders.AddAsync(folder, token);
            await connections.AddAsync(connection, token);
        }, cancellationToken);

        Assert.NotNull(await folders.GetByIdAsync(folder.Id, cancellationToken));
        Assert.NotNull(await connections.GetByIdAsync(connection.Id, cancellationToken));

        var rolledBackTag = new TagBuilder().Build();
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteAsync(async token =>
        {
            await tags.AddAsync(rolledBackTag, token);
            throw new InvalidOperationException("Force rollback.");
        }, cancellationToken));
        Assert.Null(await tags.GetByIdAsync(rolledBackTag.Id, cancellationToken));
    }
}
