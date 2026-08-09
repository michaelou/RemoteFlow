using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Microsoft.EntityFrameworkCore;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.Persistence.Queries;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Connections;
using RemoteFlow.UI.Views.Connections;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class ConnectionEditorTests
{
    /// <summary>
    /// The editor opens beside the button that opened it. Someone working by keyboard has already
    /// pressed Enter on "New connection"; if focus stays on that button they have to tab through the
    /// whole page to reach the first field, which is the difference between a usable form and a
    /// theoretical one.
    /// </summary>
    [AvaloniaFact]
    public async Task OpeningTheEditorPutsTheKeyboardInTheFirstField()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var view = new ConnectionEditorView { DataContext = await fixture.CreateEditorAsync(null, token) };
        var window = new Window { Content = view };

        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var nameBox = view.FindControl<TextBox>("NameBox");
        Assert.NotNull(nameBox);
        Assert.True(nameBox.IsFocused, "The editor opened without putting the keyboard in the Name field.");
        window.Close();
    }

    [Fact]
    public async Task ProtocolSwitchesSectionsAndOnlyReplacesThePreviousDefaultPort()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);

        editor.Protocol = ProtocolType.Rdp;

        Assert.Equal(3389, editor.Port);
        Assert.True(editor.IsRdpSectionVisible);
        Assert.False(editor.IsSshSectionVisible);

        editor.Port = 3390;
        editor.Protocol = ProtocolType.Ssh;

        Assert.Equal(3390, editor.Port);
        Assert.True(editor.IsSshSectionVisible);
    }

    [Fact]
    public async Task InlineValidationBlocksSaveAndDirtyStateIsObservable()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);

        editor.Name = "";
        editor.Host = "";
        editor.Port = 0;
        var saved = await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token);

        Assert.False(saved);
        Assert.NotNull(editor.NameError);
        Assert.NotNull(editor.HostError);
        Assert.NotNull(editor.PortError);
        Assert.True(editor.IsDirty);
        Assert.Empty(await fixture.Connections.ListAsync(token));
    }

    [Fact]
    public async Task SavingSecretWritesProviderAndPersistsOnlyOpaqueReference()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Production";
        editor.Host = "prod.test";
        editor.Username = "operator";
        editor.AuthMethod = AuthMethod.Password;
        var secret = "correct horse battery staple".ToCharArray();

        var saved = await editor.SaveAsync(secret.AsMemory(), token);

        Assert.True(saved);
        Assert.Equal("correct horse battery staple", fixture.Provider.LastSecret);
        var connection = Assert.Single(await fixture.Connections.ListAsync(token));
        Assert.Equal(CredentialKind.Password, connection.Credential.Kind);
        Assert.Equal(fixture.Provider.Name, connection.Credential.StoreProvider);
        Assert.StartsWith("remoteflow/connection/", connection.Credential.StoreKey, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse", connection.Credential.StoreKey, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse battery staple", await ReadAllTextColumnsAsync(fixture.Database, token), StringComparison.Ordinal);
        Assert.All(secret, character => Assert.Equal('\0', character));
    }

    [Fact]
    public async Task MissingProviderSecretShowsUnavailableAndSupportsReentryState()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Missing secret",
            "missing.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        _ = connection.SetCredential(
            CredentialRef.Create(CredentialKind.Password, "missing-key", fixture.Provider.Name, fixture.Clock.UtcNow).Value,
            SystemGuidProvider.Instance,
            fixture.Clock.UtcNow);
        await fixture.Connections.AddAsync(connection, token);

        var editor = await fixture.CreateEditorAsync(connection.Id, token);

        Assert.Equal(CredentialStorageStatus.UnavailableOnThisMachine, editor.CredentialStatus);
        Assert.Equal("unavailable on this machine", editor.CredentialStatusText);
        Assert.Equal("Re-enter credential", editor.CredentialActionLabel);
    }

    /// <summary>
    /// Strict never prompts, so a connection created with it can never store a host key and can
    /// never connect. New connections have to pick up the configured default instead.
    /// </summary>
    [Fact]
    public async Task NewConnectionsTakeTheConfiguredHostKeyPolicyRatherThanStrict()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Trust on first use";
        editor.Host = "tofu.test";

        Assert.Equal(HostKeyPolicy.TrustOnFirstUse, editor.HostKeyPolicy);
        Assert.True(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        var connection = Assert.Single(await fixture.Connections.ListAsync(token));
        Assert.Equal(HostKeyPolicy.TrustOnFirstUse, connection.Ssh.HostKeyPolicy);
    }

    [Fact]
    public async Task HostKeyPolicyIsEditableAndRoundTripsThroughTheEditor()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        await fixture.Settings.Set(SettingKeys.DefaultHostKeyPolicy, HostKeyPolicy.Strict, token);
        var editor = await fixture.CreateEditorAsync(null, token);
        editor.Name = "Pinned";
        editor.Host = "pinned.test";

        Assert.Equal(HostKeyPolicy.Strict, editor.HostKeyPolicy);

        editor.HostKeyPolicy = HostKeyPolicy.TrustOnFirstUse;
        Assert.True(editor.IsDirty);
        Assert.True(await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token));

        var connection = Assert.Single(await fixture.Connections.ListAsync(token));
        var reopened = await fixture.CreateEditorAsync(connection.Id, token);

        Assert.Equal(HostKeyPolicy.TrustOnFirstUse, reopened.HostKeyPolicy);
        Assert.False(reopened.IsDirty);
    }

    [Fact]
    public async Task ColorOverridePresetsSetTheHexAndMatchEnvironmentClearsIt()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var editor = await fixture.CreateEditorAsync(null, token);
        var teal = ConnectionEditorViewModel.ColorOverrideChoices.Single(choice => choice.Label == "Teal");

        Assert.Equal("Match environment", editor.SelectedColorOverride.Label);
        Assert.False(editor.IsCustomColorVisible);

        editor.SelectedColorOverride = teal;

        Assert.Equal(teal.Hex, editor.ColorOverrideHex);
        Assert.False(editor.IsCustomColorVisible);
        Assert.Equal(
            Color.Parse(teal.Hex!),
            Assert.IsType<SolidColorBrush>(editor.EnvironmentPreviewBrush).Color);

        editor.Name = "Prod";
        editor.Host = "prod.test";
        var saved = await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token);

        Assert.True(saved);
        Assert.Equal(teal.Hex, Assert.Single(await fixture.Connections.ListAsync(token)).ColorOverrideHex);

        editor.SelectedColorOverride = ConnectionEditorViewModel.ColorOverrideChoices[0];

        Assert.Null(editor.ColorOverrideHex);
    }

    [Fact]
    public async Task ColorOutsideThePresetsOpensTheCustomHexBoxAndReportsBadValues()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Bespoke",
            "bespoke.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        _ = connection.SetDetails(
            null,
            AuthMethod.None,
            null,
            EnvironmentKind.Unspecified,
            "#123456",
            SystemGuidProvider.Instance,
            fixture.Clock.UtcNow);
        await fixture.Connections.AddAsync(connection, token);

        var editor = await fixture.CreateEditorAsync(connection.Id, token);

        Assert.True(editor.SelectedColorOverride.IsCustom);
        Assert.True(editor.IsCustomColorVisible);
        Assert.Equal("#123456", editor.ColorOverrideHex);

        editor.ColorOverrideHex = "not-a-colour";
        var saved = await editor.SaveAsync(ReadOnlyMemory<char>.Empty, token);

        Assert.False(saved);
        Assert.NotNull(editor.ColorOverrideError);
        Assert.True(editor.IsCustomColorVisible);
    }

    [Fact]
    public void PublicEditorSurfaceHasNoReadableSecretProperty()
    {
        var riskyNames = new[] { "password", "secret", "passphrase" };
        var publicReadableProperties = typeof(ConnectionEditorViewModel)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead)
            .ToArray();

        Assert.DoesNotContain(publicReadableProperties, property =>
            property.PropertyType == typeof(string) &&
            riskyNames.Any(name => property.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task NavigatingAwayFromDirtyEditorRequiresExplicitDiscard()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var confirmation = new RecordingConfirmation(false, true);
        using var page = fixture.CreatePage(confirmation);
        page.RequestCreateConnection();
        await page.WorkspaceChangesSettled;
        page.Editor!.Name = "Draft server";

        Assert.False(await page.CanNavigateAwayAsync(token));
        Assert.NotNull(page.Editor);
        Assert.Contains("Draft server", confirmation.LastMessage, StringComparison.Ordinal);

        Assert.True(await page.CanNavigateAwayAsync(token));
        Assert.Null(page.Editor);
    }

    [Fact]
    public async Task SelectingAnotherConnectionMovesTheOpenEditorUnlessChangesAreKept()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var first = Connection.Create(
            SystemGuidProvider.Instance,
            "First server",
            "first.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        var second = Connection.Create(
            SystemGuidProvider.Instance,
            "Second server",
            "second.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        await fixture.Connections.AddAsync(first, token);
        await fixture.Connections.AddAsync(second, token);
        var confirmation = new RecordingConfirmation(false, true);
        using var page = fixture.CreatePage(confirmation);
        await page.InitializeAsync(token);

        page.SelectNode(NodeFor(page, first.Id), false);
        await page.WorkspaceChangesSettled;
        await page.Details!.EditCommand.ExecuteAsync(null);
        Assert.Equal(first.Id, page.Editor!.ConnectionId);

        page.SelectNode(NodeFor(page, second.Id), false);
        await page.WorkspaceChangesSettled;

        Assert.Equal(second.Id, page.Editor!.ConnectionId);
        Assert.Equal("Second server", page.Editor.Name);

        // Unsaved work still wins: the declined prompt leaves the editor on the connection it was on.
        page.Editor.Name = "Renamed but not saved";
        page.SelectNode(NodeFor(page, first.Id), false);
        await page.WorkspaceChangesSettled;

        Assert.Equal(second.Id, page.Editor!.ConnectionId);
        Assert.Contains("Renamed but not saved", confirmation.LastMessage, StringComparison.Ordinal);

        page.SelectNode(NodeFor(page, first.Id), false);
        await page.WorkspaceChangesSettled;

        Assert.Equal(first.Id, page.Editor!.ConnectionId);
    }

    [Fact]
    public async Task DetailsDeleteConfirmationNamesConnectionAndCanCancel()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditorFixture.CreateAsync(token);
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Do not delete silently",
            "safe.test",
            createdUtc: fixture.Clock.UtcNow).Value;
        await fixture.Connections.AddAsync(connection, token);
        var confirmation = new RecordingConfirmation(false, true);
        using var page = fixture.CreatePage(confirmation);
        await page.InitializeAsync(token);
        var node = page.RootNodes.Single(item =>
            item.Kind == ExplorerNodeKind.Connection && item.Id == connection.Id);
        page.SelectNode(node, false);
        await page.WorkspaceChangesSettled;

        await page.Details!.DeleteCommand.ExecuteAsync(null);

        Assert.NotNull(await fixture.Connections.GetByIdAsync(connection.Id, token));
        Assert.Contains(connection.Name, confirmation.LastMessage, StringComparison.Ordinal);

        await page.Details.DeleteCommand.ExecuteAsync(null);

        Assert.Null(await fixture.Connections.GetByIdAsync(connection.Id, token));
    }

    [Theory]
    [InlineData(ProtocolType.Ssh, true, false)]
    [InlineData(ProtocolType.Sftp, true, false)]
    [InlineData(ProtocolType.Rdp, false, true)]
    public void DetailsActionsFollowCapabilities(
        ProtocolType protocol,
        bool canOpenSftp,
        bool canLaunchRdp)
    {
        var connection = Connection.Create(
            SystemGuidProvider.Instance,
            "Server",
            "server.test",
            protocol,
            DateTimeOffset.UtcNow).Value;
        var details = new ConnectionDetailsViewModel(
            connection,
            "No folder",
            [],
            null,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask);

        Assert.Equal(canOpenSftp, details.OpenSftpCommand.CanExecute(null));
        Assert.Equal(canLaunchRdp, details.LaunchRdpCommand.CanExecute(null));
    }

    private static ExplorerNodeViewModel NodeFor(ConnectionsPageViewModel page, Guid connectionId)
    {
        return page.RootNodes.Single(node =>
            node.Kind == ExplorerNodeKind.Connection && node.Id == connectionId);
    }

    private static async Task<string> ReadAllTextColumnsAsync(
        SqliteTempDbFixture database,
        CancellationToken cancellationToken)
    {
        await using var context = await database.Factory.CreateDbContextAsync(cancellationToken);
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Connections";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            for (var index = 0; index < reader.FieldCount; index++)
            {
                if (!await reader.IsDBNullAsync(index, cancellationToken) && reader.GetValue(index) is string value)
                {
                    values.Add(value);
                }
            }
        }

        return string.Join('|', values);
    }

    private sealed class EditorFixture : IAsyncDisposable
    {
        private readonly UnitOfWork _unitOfWork;
        private readonly IGuidProvider _guids = SystemGuidProvider.Instance;

        private EditorFixture(SqliteTempDbFixture database)
        {
            Database = database;
            Clock = new FakeClock(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
            Connections = new ConnectionRepository(database.Factory);
            Folders = new FolderRepository(database.Factory);
            Tags = new TagRepository(database.Factory);
            Recent = new RecentConnectionStore(database.Factory);
            _unitOfWork = new UnitOfWork(database.Factory);
            Provider = new RecordingProvider();
            Notifier = new ConnectionChangeNotifier();
            ConnectionService = new ConnectionService(
                Connections,
                Recent,
                [Provider],
                _unitOfWork,
                _guids,
                Clock,
                Notifier);
            TagService = new TagService(Tags, Connections, _unitOfWork, _guids, Clock);
            CredentialService = new ConnectionCredentialService(
                Connections,
                new FixedSelector(Provider),
                [Provider],
                _unitOfWork,
                _guids,
                Clock,
                Notifier);
            FolderService = new FolderService(
                Folders,
                Connections,
                ConnectionService,
                _unitOfWork,
                _guids,
                Clock);
            Queries = new ConnectionQueryService(database.Factory);
            Settings = new InMemorySettingsStore();
        }

        public SqliteTempDbFixture Database { get; }
        public FakeClock Clock { get; }
        public ConnectionRepository Connections { get; }
        public FolderRepository Folders { get; }
        public TagRepository Tags { get; }
        public RecentConnectionStore Recent { get; }
        public RecordingProvider Provider { get; }
        public ConnectionService ConnectionService { get; }
        public TagService TagService { get; }
        public ConnectionCredentialService CredentialService { get; }
        public FolderService FolderService { get; }
        public ConnectionQueryService Queries { get; }
        public InMemorySettingsStore Settings { get; }
        public ConnectionChangeNotifier Notifier { get; }

        public static async Task<EditorFixture> CreateAsync(CancellationToken cancellationToken)
        {
            return new EditorFixture(await SqliteTempDbFixture.CreateAsync(cancellationToken));
        }

        public async Task<ConnectionEditorViewModel> CreateEditorAsync(
            Guid? connectionId,
            CancellationToken cancellationToken)
        {
            var editor = new ConnectionEditorViewModel(
                ConnectionService,
                Connections,
                CredentialService,
                Folders,
                Tags,
                TagService,
                settings: Settings);
            await editor.InitializeAsync(connectionId, cancellationToken);
            return editor;
        }

        public ConnectionsPageViewModel CreatePage(IConfirmationDialogService confirmation)
        {
            var factory = new ConnectionEditorViewModelFactory(
                ConnectionService,
                Connections,
                CredentialService,
                Folders,
                Tags,
                TagService,
                Recent);
            return new ConnectionsPageViewModel(
                Queries,
                Folders,
                Tags,
                ConnectionService,
                FolderService,
                Recent,
                Settings,
                new SuccessfulOpener(),
                Notifier,
                _guids,
                Clock,
                factory,
                confirmation);
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class FixedSelector(ICredentialProvider provider) : ICredentialProviderSelector
    {
        public Task<ICredentialProvider> SelectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(provider);
        }
    }

    private sealed class RecordingProvider : ICredentialProvider
    {
        private readonly Dictionary<string, char[]> _secrets = new(StringComparer.Ordinal);

        public string Name => "test-store";
        public bool IsAvailable => true;
        public string? LastSecret { get; private set; }

        public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_secrets.TryGetValue(storeKey, out var value) ? new SecretHandle(value) : null);
        }

        public Task SetAsync(
            string storeKey,
            ReadOnlyMemory<char> secret,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastSecret = new string(secret.Span);
            _secrets[storeKey] = secret.ToArray();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = _secrets.Remove(storeKey);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingConfirmation(params bool[] results) : IConfirmationDialogService
    {
        private readonly Queue<bool> _results = new(results);

        public string LastMessage { get; private set; } = string.Empty;

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string confirmLabel,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastMessage = message;
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class SuccessfulOpener : IConnectionSessionOpener
    {
        public Task<ConnectionOpenResult> OpenAsync(
            Guid connectionId,
            ConnectionOpenMode mode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ConnectionOpenResult.Success());
        }
    }
}
