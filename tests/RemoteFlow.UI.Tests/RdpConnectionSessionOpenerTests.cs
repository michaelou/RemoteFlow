using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Connections;
using RemoteFlow.UI.ViewModels.Settings;
using RemoteFlow.UI.ViewModels.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class RdpConnectionSessionOpenerTests
{
    [Fact]
    public async Task WindowsDefaultOpensPreparedConnectedWorkspaceTab()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = CreateFixture(platformAvailable: true, embeddedSupported: true);

        var result = await fixture.Opener.OpenAsync(fixture.Connection.Id, ConnectionOpenMode.Default, token);

        Assert.True(result.Opened);
        var tab = Assert.Single(fixture.Workspace.Sessions);
        Assert.Same(fixture.EmbeddedFactory.CreatedTab, tab);
        Assert.True(fixture.EmbeddedFactory.CreatedTab!.Prepared);
        Assert.True(fixture.EmbeddedFactory.CreatedTab.Connected);
        Assert.Equal("terminals", fixture.Navigation.CurrentPageKey);
        Assert.Equal(0, fixture.ExternalLauncher.LaunchCount);
    }

    [Fact]
    public async Task SettingAndExplicitActionRouteToUnchangedExternalLauncherImmediately()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = CreateFixture(platformAvailable: true, embeddedSupported: true);
        await fixture.Settings.Set(SettingKeys.WindowsRdpOpenMode, WindowsRdpOpenMode.External, token);

        var bySetting = await fixture.Opener.OpenAsync(fixture.Connection.Id, ConnectionOpenMode.Rdp, token);
        await fixture.Settings.Set(SettingKeys.WindowsRdpOpenMode, WindowsRdpOpenMode.Embedded, token);
        var explicitExternal = await fixture.Opener.OpenAsync(
            fixture.Connection.Id,
            ConnectionOpenMode.RdpExternal,
            token);

        Assert.True(bySetting.Opened);
        Assert.True(explicitExternal.Opened);
        Assert.Equal(2, fixture.ExternalLauncher.LaunchCount);
        Assert.Equal(0, fixture.EmbeddedFactory.CreateCount);
        Assert.Empty(fixture.Workspace.Sessions);
    }

    [Fact]
    public async Task NonWindowsKeepsTheExistingExternalOnlyPath()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = CreateFixture(platformAvailable: false, embeddedSupported: false);

        var result = await fixture.Opener.OpenAsync(fixture.Connection.Id, ConnectionOpenMode.Rdp, token);

        Assert.True(result.Opened);
        Assert.Equal(1, fixture.ExternalLauncher.LaunchCount);
        Assert.Equal(0, fixture.EmbeddedFactory.CreateCount);
        Assert.Empty(fixture.Workspace.Sessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WindowsEmbeddedFailureOffersOneStepExternalRecovery(bool failCreation)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = CreateFixture(
            platformAvailable: true,
            embeddedSupported: failCreation,
            failCreation: failCreation);

        var failed = await fixture.Opener.OpenAsync(fixture.Connection.Id, ConnectionOpenMode.Rdp, token);

        Assert.False(failed.Opened);
        Assert.Contains("external RDP client", failed.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Open in external RDP client", failed.RecoveryActionLabel);
        Assert.Equal(ConnectionOpenMode.RdpExternal, failed.RecoveryMode);

        var recovered = await fixture.Opener.OpenAsync(
            fixture.Connection.Id,
            failed.RecoveryMode!.Value,
            token);
        Assert.True(recovered.Opened);
        Assert.Equal(1, fixture.ExternalLauncher.LaunchCount);
    }

    [Fact]
    public async Task WindowsRdpSettingDefaultsEmbeddedPersistsAndIsAbsentOffPlatform()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        var available = new RdpSettingsViewModel(settings, new RecordingEmbeddedFactory(true, true));
        await available.InitializeAsync(token);
        Assert.Equal(WindowsRdpOpenMode.Embedded, available.SelectedOpenMode!.Value);

        available.SelectedOpenMode = available.OpenModes.Single(option => option.Value == WindowsRdpOpenMode.External);
        await available.FlushAsync();
        Assert.Equal(WindowsRdpOpenMode.External, await settings.Get(SettingKeys.WindowsRdpOpenMode, token));

        var unavailable = new RdpSettingsViewModel(settings, new RecordingEmbeddedFactory(false, false));
        await unavailable.InitializeAsync(token);
        Assert.False(unavailable.IsAvailable);
        Assert.Null(unavailable.SelectedOpenMode);
    }

    [Fact]
    public async Task ConnectionDetailsAlwaysExposesDefaultAndWindowsExternalActions()
    {
        var modes = new List<ConnectionOpenMode>();
        var connection = CreateRdpConnection();
        var details = new ConnectionDetailsViewModel(
            connection,
            "No folder",
            [],
            null,
            mode =>
            {
                modes.Add(mode);
                return Task.CompletedTask;
            },
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            showExplicitExternalRdpAction: true);

        await details.LaunchRdpCommand.ExecuteAsync(null);
        await details.OpenExternalRdpCommand.ExecuteAsync(null);

        Assert.True(details.ShowExplicitExternalRdpAction);
        Assert.Equal([ConnectionOpenMode.Rdp, ConnectionOpenMode.RdpExternal], modes);
    }

    private static OpenerFixture CreateFixture(
        bool platformAvailable,
        bool embeddedSupported,
        bool failCreation = false)
    {
        var connection = CreateRdpConnection();
        var settings = new InMemorySettingsStore();
        var embeddedFactory = new RecordingEmbeddedFactory(platformAvailable, embeddedSupported)
        {
            FailCreation = failCreation,
        };
        var launcher = new RecordingRdpLauncher();
        var workspace = new TerminalsPageViewModel();
        var navigation = NavigationService.CreateDefault();
        var services = new ServiceCollection()
            .AddSingleton<IConnectionRepository>(new SingleConnectionRepository(connection))
            .AddSingleton<ISettingsStore>(settings)
            .AddSingleton<IEmbeddedRdpWorkspaceSessionFactory>(embeddedFactory)
            .AddSingleton<IRdpLauncher>(launcher)
            .AddSingleton<INavigationService>(navigation)
            .AddSingleton(workspace)
            .BuildServiceProvider();
        return new(
            connection,
            settings,
            embeddedFactory,
            launcher,
            workspace,
            navigation,
            services,
            new SshConnectionSessionOpener(new UnusedSessionManager(), services));
    }

    private static Connection CreateRdpConnection()
    {
        return Connection.Create(
            SystemGuidProvider.Instance,
            "DC01",
            "dc01.example.test",
            ProtocolType.Rdp,
            DateTimeOffset.UtcNow).Value;
    }

    private sealed record OpenerFixture(
        Connection Connection,
        InMemorySettingsStore Settings,
        RecordingEmbeddedFactory EmbeddedFactory,
        RecordingRdpLauncher ExternalLauncher,
        TerminalsPageViewModel Workspace,
        NavigationService Navigation,
        ServiceProvider Services,
        SshConnectionSessionOpener Opener) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Workspace.DisposeAsync();
            await Services.DisposeAsync();
        }
    }

    private sealed class RecordingEmbeddedFactory(bool platformAvailable, bool embeddedSupported) :
        IEmbeddedRdpWorkspaceSessionFactory
    {
        public bool FailCreation { get; init; }

        public bool IsAvailableOnCurrentPlatform { get; } = platformAvailable;

        public bool SupportsEmbeddedSessions { get; } = embeddedSupported;

        public int CreateCount { get; private set; }

        public RecordingEmbeddedTab? CreatedTab { get; private set; }

        public Task<Result<IEmbeddedRdpWorkspaceSession>> CreateAsync(
            Connection connection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCount++;
            if (FailCreation)
            {
                return Task.FromResult(Result<IEmbeddedRdpWorkspaceSession>.Failure(RemoteFlowError.Unavailable(
                    "embedded_rdp.test_failure",
                    "The embedded control failed to initialize.")));
            }

            CreatedTab = new RecordingEmbeddedTab(connection.Name, connection.Environment);
            return Task.FromResult(Result<IEmbeddedRdpWorkspaceSession>.Success(CreatedTab));
        }
    }

    private sealed class RecordingEmbeddedTab(string title, EnvironmentKind environment) :
        IEmbeddedRdpWorkspaceSession
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title { get; } = title;
        public string TabTitle => Title;
        public EnvironmentKind Environment { get; } = environment;
        public string AccentColorHex => "#FF7B72";
        public string TabBackgroundHex => "#121821";
        public string ChromeTintHex => "#101418";
        public string EnvironmentCue => "PROD !";
        public string ProtocolCue => "RDP";
        public string StatusText => Connected ? "Connected" : "Created";
        public string TabAccessibleName => $"{Title}, RDP, production, {StatusText}";
        public string CloseTabAccessibleName => $"Close RDP session {Title}";
        public bool IsActive { get; private set; }
        public bool IsTiled { get; private set; }
        public bool IsContentVisible => IsTiled || IsActive;
        public bool IsLive => true;
        public bool IsEnded => false;
        public bool CanOpenInSystemTerminal => false;
        public string? EndedMessage => null;
        public string RecoveryActionLabel => "Reconnect";
        public IAsyncRelayCommand RetryCommand { get; } = new AsyncRelayCommand(() => Task.CompletedTask);
        public bool Prepared { get; private set; }
        public bool Connected { get; private set; }

        public void PrepareForConnect()
        {
            Prepared = true;
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connected = true;
            return Task.CompletedTask;
        }

        public void SetActive(bool isActive)
        {
            IsActive = isActive;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsContentVisible)));
        }

        public void SetTiled(bool isTiled)
        {
            IsTiled = isTiled;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTiled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsContentVisible)));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRdpLauncher : IRdpLauncher
    {
        public string MissingClientGuidance => string.Empty;

        public int LaunchCount { get; private set; }

        public Task<RdpLaunchResult> LaunchAsync(
            Connection connection,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaunchCount++;
            return Task.FromResult(RdpLaunchResult.Launched);
        }

        public Task<IReadOnlyList<RdpClientInfo>> DetectClientsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RdpClientInfo>>([]);
        }

        public Task SweepStaleFilesAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class SingleConnectionRepository(Connection connection) : IConnectionRepository
    {
        public Task<Connection?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Connection?>(id == connection.Id ? connection : null);
        }

        public Task<IReadOnlyList<Connection>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Connection>>([connection]);
        }

        public Task AddAsync(Connection item, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task UpdateAsync(Connection item, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<bool> AddTagAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> RemoveTagAsync(Guid connectionId, Guid tagId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class UnusedSessionManager : ISessionManager
    {
        public event EventHandler<ManagedSshSession>? SessionAdded { add { } remove { } }
        public event EventHandler<ManagedSshSession>? SessionRemoved { add { } remove { } }
        public event EventHandler<SessionTransitionEventArgs>? SessionChanged { add { } remove { } }
        public IReadOnlyList<ManagedSshSession> Sessions => [];

        public Task<ManagedSshSession> OpenAsync(Guid connectionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IReadOnlyList<ManagedSshSession> GetForConnection(Guid connectionId)
        {
            return [];
        }

        public Task RetryAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CancelAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CloseAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
