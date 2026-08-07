using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Validation;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class ConnectionAndTagServiceTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Duplicate_CopiesDetailsAndTagsButNotCredential()
    {
        var store = new InMemoryStore();
        var source = new ConnectionBuilder().WithName("Production").Build();
        var tag = new TagBuilder().Build();
        _ = source.AddTag(tag.Id);
        _ = source.SetCredential(
            CredentialRef.Create(CredentialKind.Password, "credential-key", "fake", _now).Value,
            new FakeGuidProvider(),
            _now);
        store.Connections.Add(source);
        store.Tags.Add(tag);
        var service = CreateConnectionService(store);

        var result = await service.DuplicateAsync(source.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(source.Id, result.Value.Id);
        Assert.Equal("Production (copy)", result.Value.Name);
        _ = Assert.Single(result.Value.Tags);
        Assert.Equal(tag.Id, result.Value.Tags.Single().TagId);
        Assert.True(result.Value.Credential.IsEmpty);
    }

    [Fact]
    public async Task Delete_RemovesCredentialRecentEntryAndConnection()
    {
        var store = new InMemoryStore();
        var source = new ConnectionBuilder().Build();
        _ = source.SetCredential(
            CredentialRef.Create(CredentialKind.Password, "credential-key", "fake", _now).Value,
            new FakeGuidProvider(),
            _now);
        store.Connections.Add(source);
        _ = store.RecentIds.Add(source.Id);
        var credentialProvider = new RecordingCredentialProvider();
        var service = CreateConnectionService(store, credentialProvider);

        var result = await service.DeleteAsync(source.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(source, store.Connections);
        Assert.DoesNotContain(source.Id, store.RecentIds);
        Assert.Contains("credential-key", credentialProvider.DeletedKeys);
    }

    [Fact]
    public async Task Update_ResetsOnlyAnUnchangedProtocolDefaultPort()
    {
        var store = new InMemoryStore();
        var defaultPort = new ConnectionBuilder().Build();
        var customPort = new ConnectionBuilder()
            .WithName("Custom")
            .WithGuidProvider(new SequentialGuidProvider(100))
            .Build();
        _ = customPort.ChangeEndpoint(
            customPort.Host,
            2222,
            ProtocolType.Ssh,
            new FakeGuidProvider(),
            _now);
        store.Connections.Add(defaultPort);
        store.Connections.Add(customPort);
        var service = CreateConnectionService(store);

        var reset = await service.UpdateAsync(
            defaultPort.Id,
            new ConnectionInput(defaultPort.Name, defaultPort.Host, 22, ProtocolType.Rdp),
            TestContext.Current.CancellationToken);
        var preserved = await service.UpdateAsync(
            customPort.Id,
            new ConnectionInput(customPort.Name, customPort.Host, 2222, ProtocolType.Rdp),
            TestContext.Current.CancellationToken);

        Assert.Equal(3389, reset.Value.Port);
        Assert.Equal(2222, preserved.Value.Port);
        Assert.Equal(_now, reset.Value.ModifiedUtc);
    }

    [Fact]
    public void Validator_ReturnsUserReadyConditionalMessages()
    {
        var errors = ConnectionValidator.Validate(new ConnectionInput(
            "Server",
            "example.com",
            22,
            ProtocolType.Ssh,
            AuthMethod: AuthMethod.PrivateKey));

        Assert.Contains(errors, error => error.Code == "connection.username" && error.Message.Contains("username", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Code == "connection.private_key_path" && error.Message.Contains("private key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(errors, error => error.Message.Contains(nameof(AuthMethod.PrivateKey), StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreateTag_ReusesExistingNameIgnoringCase()
    {
        var store = new InMemoryStore();
        var existing = new TagBuilder().WithName("prod").Build();
        store.Tags.Add(existing);
        var service = CreateTagService(store);

        var result = await service.CreateAsync("Prod", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Same(existing, result.Value);
        _ = Assert.Single(store.Tags);
    }

    [Fact]
    public async Task Merge_MovesAndDeduplicatesAssignmentsAndUpdatesUsageCounts()
    {
        var store = new InMemoryStore();
        var source = new TagBuilder().WithName("Old").Build();
        var target = new TagBuilder()
            .WithName("New")
            .WithGuidProvider(new SequentialGuidProvider(100))
            .Build();
        var first = new ConnectionBuilder().WithName("First").Build();
        var second = new ConnectionBuilder()
            .WithName("Second")
            .WithGuidProvider(new SequentialGuidProvider(200))
            .Build();
        _ = first.AddTag(source.Id);
        _ = first.AddTag(target.Id);
        _ = second.AddTag(source.Id);
        store.Tags.AddRange([source, target]);
        store.Connections.AddRange([first, second]);
        var service = CreateTagService(store);

        var merged = await service.MergeAsync(source.Id, target.Id, TestContext.Current.CancellationToken);
        var usage = await service.GetUsageCountsAsync(TestContext.Current.CancellationToken);

        Assert.True(merged.IsSuccess);
        Assert.DoesNotContain(source, store.Tags);
        Assert.All(store.Connections, connection =>
        {
            Assert.DoesNotContain(connection.Tags, item => item.TagId == source.Id);
            _ = Assert.Single(connection.Tags, item => item.TagId == target.Id);
        });
        Assert.Equal(2, Assert.Single(usage).ConnectionCount);
    }

    [Fact]
    public async Task DeleteTag_RemovesAssignmentsButNotConnections()
    {
        var store = new InMemoryStore();
        var tag = new TagBuilder().Build();
        var connection = new ConnectionBuilder().Build();
        _ = connection.AddTag(tag.Id);
        store.Tags.Add(tag);
        store.Connections.Add(connection);
        var service = CreateTagService(store);

        var result = await service.DeleteAsync(tag.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        _ = Assert.Single(store.Connections);
        Assert.Empty(connection.Tags);
    }

    private static ConnectionService CreateConnectionService(
        InMemoryStore store,
        RecordingCredentialProvider? provider = null)
    {
        return new ConnectionService(
            store,
            store,
            [provider ?? new RecordingCredentialProvider()],
            store,
            new SequentialGuidProvider(1000),
            new FakeClock(_now));
    }

    private static TagService CreateTagService(InMemoryStore store)
    {
        return new TagService(
            store,
            store,
            store,
            new SequentialGuidProvider(1000),
            new FakeClock(_now));
    }

    private sealed class RecordingCredentialProvider : ICredentialProvider
    {
        public string Name => "fake";

        public bool IsAvailable => true;

        public List<string> DeletedKeys { get; } = [];

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<SecretHandle?>(null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            DeletedKeys.Add(storeKey);
            return Task.CompletedTask;
        }
    }

    private sealed class SequentialGuidProvider(int value) : IGuidProvider
    {
        private int _value = value;

        public Guid NewGuid()
        {
            return new Guid(_value++, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
        }
    }

    private sealed class InMemoryStore : IConnectionRepository, ITagRepository, IRecentConnectionStore, IUnitOfWork
    {
        public List<Connection> Connections { get; } = [];

        public List<Tag> Tags { get; } = [];

        public HashSet<Guid> RecentIds { get; } = [];

        public Task<Connection?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Connections.SingleOrDefault(connection => connection.Id == id));
        }

        public Task<IReadOnlyList<Connection>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Connection>>([.. Connections]);
        }

        public Task AddAsync(Connection connection, CancellationToken cancellationToken = default)
        {
            Connections.Add(connection);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Connection connection, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var connection = Connections.SingleOrDefault(candidate => candidate.Id == id);
            if (connection is not null)
            {
                _ = Connections.Remove(connection);
            }

            return Task.CompletedTask;
        }

        Task ITagRepository.DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            var tag = Tags.SingleOrDefault(candidate => candidate.Id == id);
            if (tag is not null)
            {
                _ = Tags.Remove(tag);
                foreach (var item in Connections)
                {
                    _ = item.RemoveTag(id);
                }
            }

            return Task.CompletedTask;
        }

        public Task<bool> AddTagAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default)
        {
            var result = Connections.Single(connection => connection.Id == connectionId).AddTag(tagId);
            return Task.FromResult(result.IsSuccess);
        }

        public Task<bool> RemoveTagAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Connections.Single(connection => connection.Id == connectionId).RemoveTag(tagId));
        }

        Task<Tag?> ITagRepository.GetByIdAsync(Guid id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Tags.SingleOrDefault(tag => tag.Id == id));
        }

        public Task<Tag?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Tags.SingleOrDefault(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)));
        }

        Task<IReadOnlyList<Tag>> ITagRepository.ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<Tag>>([.. Tags]);
        }

        public Task AddAsync(Tag tag, CancellationToken cancellationToken = default)
        {
            Tags.Add(tag);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Tag tag, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<int> GetUsageCountAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Connections.Count(connection => connection.Tags.Any(item => item.TagId == id)));
        }

        public Task<RecentConnection?> GetAsync(Guid connectionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<RecentConnection?>(null);
        }

        public Task<IReadOnlyList<RecentConnection>> ListAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RecentConnection>>([]);
        }

        public Task RecordOpenedAsync(
            Guid connectionId,
            DateTimeOffset openedUtc,
            CancellationToken cancellationToken = default)
        {
            _ = RecentIds.Add(connectionId);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid connectionId, CancellationToken cancellationToken = default)
        {
            _ = RecentIds.Remove(connectionId);
            return Task.CompletedTask;
        }

        public Task ExecuteAsync(
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default)
        {
            return operation(cancellationToken);
        }

        public Task<TResult> ExecuteAsync<TResult>(
            Func<CancellationToken, Task<TResult>> operation,
            CancellationToken cancellationToken = default)
        {
            return operation(cancellationToken);
        }
    }
}
