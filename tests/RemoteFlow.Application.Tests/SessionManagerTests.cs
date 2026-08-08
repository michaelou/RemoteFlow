using System.Text;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class SessionManagerTests
{
    [Fact]
    public void StateMachineRejectsIllegalTransitionsAndRaisesOncePerTransition()
    {
        var channel = new FakeSshShell();
        var session = new ManagedSshSession(
            Guid.NewGuid(), Guid.NewGuid(), "web-01", EnvironmentKind.Production, "#FF0000", channel);
        var transitions = new List<SessionTransitionEventArgs>();
        session.Transitioned += (_, eventArgs) => transitions.Add(eventArgs);

        session.TransitionTo(SessionState.Connecting);
        session.TransitionTo(SessionState.Connected);

        Assert.Equal(2, transitions.Count);
        Assert.Equal(SessionState.Created, transitions[0].PreviousState);
        Assert.Equal(SessionState.Connected, transitions[1].CurrentState);
        _ = Assert.Throws<InvalidOperationException>(() => session.TransitionTo(SessionState.Reconnecting));
        Assert.Equal(2, transitions.Count);
    }

    [Fact]
    public async Task ThreeSessionsForSameConnectionAreIndependentAndDisambiguated()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();

        var first = await fixture.Manager.OpenAsync(fixture.Connection.Id, token);
        var second = await fixture.Manager.OpenAsync(fixture.Connection.Id, token);
        var third = await fixture.Manager.OpenAsync(fixture.Connection.Id, token);

        Assert.Equal(["web-01", "web-01 (2)", "web-01 (3)"], [first.Title, second.Title, third.Title]);
        Assert.Equal(3, fixture.Transport.Connections.Count);
        await fixture.Manager.CloseAsync(first.SessionId, token);
        Assert.True(fixture.Transport.Connections[0].IsDisconnected);
        Assert.False(fixture.Transport.Connections[1].IsDisconnected);
        Assert.False(fixture.Transport.Connections[2].IsDisconnected);
        Assert.Equal(2, fixture.Manager.GetForConnection(fixture.Connection.Id).Count);

        await fixture.Manager.DisposeAsync();
    }

    [Fact]
    public async Task FailureCanRetryAndRecentIsRecordedOnlyAfterSuccess()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Transport.FailNextConnect(SshError.AuthFailed, "Password rejected; try again.");

        var session = await fixture.Manager.OpenAsync(fixture.Connection.Id, token);

        Assert.Equal(SessionState.Failed, session.State);
        Assert.Contains("try again", session.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Recent.RecordCount);

        await fixture.Manager.RetryAsync(session.SessionId, token);

        Assert.Equal(SessionState.Connected, session.State);
        Assert.Equal(1, fixture.Recent.RecordCount);
        await fixture.Manager.DisposeAsync();
    }

    [Fact]
    public async Task StartupDirectoryAndInitialCommandAreWrittenOnConnect()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(startupDirectory: "/srv/app data", initialCommand: "printf ready");

        _ = await fixture.Manager.OpenAsync(fixture.Connection.Id, token);
        var writes = fixture.Transport.LastConnection!.LastShell!.Writes;
        var startup = Encoding.UTF8.GetString(Assert.Single(writes));

        Assert.Contains("cd -- '/srv/app data'", startup, StringComparison.Ordinal);
        Assert.Contains("printf ready", startup, StringComparison.Ordinal);
        await fixture.Manager.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownClosesEveryConnectionWithinBound()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        _ = await fixture.Manager.OpenAsync(fixture.Connection.Id, token);
        _ = await fixture.Manager.OpenAsync(fixture.Connection.Id, token);

        await fixture.Manager.ShutdownAsync(TimeSpan.FromSeconds(2), token);

        Assert.Empty(fixture.Manager.Sessions);
        Assert.All(fixture.Transport.Connections, connection => Assert.True(connection.IsDisconnected));
    }

    private static Fixture CreateFixture(string? startupDirectory = null, string? initialCommand = null)
    {
        var entityGuids = new FakeGuidProvider();
        var connection = Connection.Create(entityGuids, "web-01", "web.example", ProtocolType.Ssh).Value;
        _ = connection.SetDetails(
            "deploy",
            AuthMethod.Password,
            null,
            EnvironmentKind.Production,
            "#FF0000",
            entityGuids);
        var ssh = SshOptions.Default().Configure(
            initialCommand: initialCommand,
            startupDirectory: startupDirectory,
            hostKeyPolicy: HostKeyPolicy.TrustOnFirstUse).Value;
        _ = connection.SetOptions(ssh, SftpOptions.Default(), RdpOptions.Default(), entityGuids);
        var repository = new SingleConnectionRepository(connection);
        var transport = new FakeSshTransport();
        var recent = new RecordingRecentStore();
        var manager = new SessionManager(
            repository,
            new StaticAuthentication(),
            transport,
            recent,
            new FakeClock(new DateTimeOffset(2026, 8, 8, 1, 2, 3, TimeSpan.Zero)),
            new FakeGuidProvider());
        return new(connection, transport, recent, manager);
    }

    private sealed record Fixture(
        Connection Connection,
        FakeSshTransport Transport,
        RecordingRecentStore Recent,
        SessionManager Manager);

    private sealed class StaticAuthentication : ISshAuthenticationMaterialProvider
    {
        public Task<IReadOnlyList<SshAuthMaterial>> CreateAsync(Connection connection, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SshAuthMaterial>>([new SshAuthMaterial.Password("secret")]);
        }
    }

    private sealed class SingleConnectionRepository(Connection connection) : IConnectionRepository
    {
        public Task<Connection?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(id == connection.Id ? connection : null);
        }
        public Task<IReadOnlyList<Connection>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Connection>>([connection]);
        }
        public Task AddAsync(Connection value, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task UpdateAsync(Connection value, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
        public Task<bool> AddTagAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
        public Task<bool> RemoveTagAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }
    }

    private sealed class RecordingRecentStore : IRecentConnectionStore
    {
        public int RecordCount { get; private set; }
        public Task<RecentConnection?> GetAsync(Guid connectionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<RecentConnection?>(null);
        }
        public Task<IReadOnlyList<RecentConnection>> ListAsync(int limit, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RecentConnection>>([]);
        }
        public Task RecordOpenedAsync(Guid connectionId, DateTimeOffset openedUtc, CancellationToken cancellationToken = default)
        {
            RecordCount++;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(Guid connectionId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
