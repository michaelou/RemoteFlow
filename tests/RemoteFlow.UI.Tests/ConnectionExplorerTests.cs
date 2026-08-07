using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Persistence.Queries;
using RemoteFlow.Persistence.Repositories;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Connections;
using RemoteFlow.UI.Views.Connections;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class ConnectionExplorerTests
{
    [AvaloniaFact]
    public async Task BuildsVirtualRootsAndColorSafeProductionBadgeWithOverride()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        var folder = await fixture.AddFolderAsync("Production", cancellationToken: token);
        var connection = await fixture.AddConnectionAsync(
            "Prod server",
            folder.Id,
            EnvironmentKind.Production,
            isFavorite: true,
            colorOverrideHex: "#123456",
            cancellationToken: token);
        await fixture.Recent.RecordOpenedAsync(connection.Id, fixture.Clock.UtcNow, token);
        using var viewModel = fixture.CreateViewModel();

        await viewModel.InitializeAsync(token);

        Assert.Equal("Favorites", viewModel.RootNodes[0].Name);
        Assert.Equal("Recent", viewModel.RootNodes[1].Name);
        Assert.Equal(connection.Id, Assert.Single(viewModel.RootNodes[0].Children).Id);
        Assert.Equal(connection.Id, Assert.Single(viewModel.RootNodes[1].Children).Id);
        var node = FindRealConnection(viewModel, connection.Id);
        Assert.Equal("PROD", node.Badge!.Text);
        Assert.Equal("⚠", node.Badge.Icon);
        var brush = Assert.IsType<SolidColorBrush>(node.Badge.Background);
        Assert.Equal(Color.Parse("#123456"), brush.Color);
    }

    [Fact]
    public async Task DragDropPersistsReparentAndOrderAndRejectsInvalidTargetsWithFeedback()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        var source = await fixture.AddFolderAsync("Source", cancellationToken: token);
        var target = await fixture.AddFolderAsync("Target", cancellationToken: token);
        var connection = await fixture.AddConnectionAsync("Server", source.Id, cancellationToken: token);
        var other = await fixture.AddConnectionAsync("Other", target.Id, cancellationToken: token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);
        var node = FindRealConnection(viewModel, connection.Id);
        var targetNode = FindFolder(viewModel, target.Id);

        var moved = await viewModel.DropAsync([node], targetNode, 7, token);
        var persisted = await fixture.Connections.GetByIdAsync(connection.Id, token);
        var invalid = await viewModel.DropAsync(
            [FindRealConnection(viewModel, connection.Id)],
            FindRealConnection(viewModel, other.Id),
            cancellationToken: token);

        Assert.True(moved);
        Assert.Equal(target.Id, persisted!.FolderId);
        Assert.Equal(7, persisted.SortOrder);
        Assert.False(invalid);
        Assert.Contains("onto a folder", viewModel.FeedbackMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpansionPersistsAndLiveConnectionChangesRefreshTheTree()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        var folder = await fixture.AddFolderAsync("Servers", cancellationToken: token);
        var connection = await fixture.AddConnectionAsync("Old name", folder.Id, cancellationToken: token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);
        var folderNode = FindFolder(viewModel, folder.Id);

        await viewModel.SetExpandedAsync(folderNode, true, token);
        var persistedFolder = await fixture.Folders.GetByIdAsync(folder.Id, token);
        var renamed = await fixture.ConnectionService.RenameAsync(connection.Id, "New name", token);
        await viewModel.ConnectionChangesSettled;

        Assert.True(persistedFolder!.IsExpanded);
        Assert.True(renamed.IsSuccess);
        Assert.Equal("New name", FindRealConnection(viewModel, connection.Id).Name);
    }

    [Fact]
    public async Task RecentRecordsSuccessfulOpensOnlyAndDisplaysTheConfiguredLimit()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        await fixture.Settings.Set(SettingKeys.RecentLimit, 2, token);
        var failed = await fixture.AddConnectionAsync("Failed", cancellationToken: token);
        var first = await fixture.AddConnectionAsync("First", cancellationToken: token);
        var second = await fixture.AddConnectionAsync("Second", cancellationToken: token);
        var third = await fixture.AddConnectionAsync("Third", cancellationToken: token);
        fixture.SessionOpener.Results.Enqueue(false);
        fixture.SessionOpener.Results.Enqueue(true);
        fixture.SessionOpener.Results.Enqueue(true);
        fixture.SessionOpener.Results.Enqueue(true);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);

        await FindRealConnection(viewModel, failed.Id).ConnectCommand.ExecuteAsync(null);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await FindRealConnection(viewModel, first.Id).ConnectCommand.ExecuteAsync(null);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await FindRealConnection(viewModel, second.Id).ConnectCommand.ExecuteAsync(null);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await FindRealConnection(viewModel, third.Id).ConnectCommand.ExecuteAsync(null);
        var recentRoot = viewModel.RootNodes.Single(node => node.Kind == ExplorerNodeKind.Recent);
        var stored = await fixture.Recent.ListAsync(10, token);

        Assert.Equal([third.Id, second.Id], [.. recentRoot.Children.Select(node => node.Id!.Value)]);
        Assert.DoesNotContain(stored, item => item.ConnectionId == failed.Id);
        Assert.Equal(3, stored.Count);
    }

    [AvaloniaFact]
    public async Task ThousandConnectionsKeepTheRealizedTreeRowsBounded()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        await using (var context = await fixture.Database.Factory.CreateDbContextAsync(token))
        {
            var connections = Enumerable.Range(0, 1_000)
                .Select(index => Connection.Create(
                    SystemGuidProvider.Instance,
                    $"Server {index:0000}",
                    $"server-{index}.test").Value)
                .ToArray();
            context.Connections.AddRange(connections);
            _ = await context.SaveChangesAsync(token);
        }

        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);
        var view = new ConnectionsView { DataContext = viewModel };
        var window = new Window { Width = 800, Height = 500, Content = view };
        window.Show();
        window.UpdateLayout();
        var realizedRows = view.GetVisualDescendants().OfType<TreeViewItem>().Count();

        Assert.InRange(realizedRows, 1, 100);
        window.Close();
    }

    private static ExplorerNodeViewModel FindFolder(ConnectionsPageViewModel viewModel, Guid id)
    {
        return Flatten(viewModel.RootNodes).Single(node => node.Kind == ExplorerNodeKind.Folder && node.Id == id);
    }

    private static ExplorerNodeViewModel FindRealConnection(ConnectionsPageViewModel viewModel, Guid id)
    {
        return viewModel.RootNodes
            .Where(node => !node.IsVirtual)
            .SelectMany(node => Flatten([node]))
            .Single(node => node.Kind == ExplorerNodeKind.Connection && node.Id == id);
    }

    private static IEnumerable<ExplorerNodeViewModel> Flatten(IEnumerable<ExplorerNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }

    private sealed class ExplorerFixture : IAsyncDisposable
    {
        private readonly IGuidProvider _guids = SystemGuidProvider.Instance;
        private readonly UnitOfWork _unitOfWork;

        private ExplorerFixture(SqliteTempDbFixture database)
        {
            Database = database;
            Clock = new FakeClock(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
            Settings = new InMemorySettingsStore();
            SessionOpener = new QueueSessionOpener();
            Notifier = new ConnectionChangeNotifier();
            Connections = new ConnectionRepository(database.Factory);
            Folders = new FolderRepository(database.Factory);
            Recent = new RecentConnectionStore(database.Factory);
            _unitOfWork = new UnitOfWork(database.Factory);
            ConnectionService = new ConnectionService(
                Connections,
                Recent,
                [],
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
        }

        public SqliteTempDbFixture Database { get; }

        public FakeClock Clock { get; }

        public InMemorySettingsStore Settings { get; }

        public QueueSessionOpener SessionOpener { get; }

        public ConnectionChangeNotifier Notifier { get; }

        public ConnectionRepository Connections { get; }

        public FolderRepository Folders { get; }

        public RecentConnectionStore Recent { get; }

        public ConnectionService ConnectionService { get; }

        public FolderService FolderService { get; }

        public ConnectionQueryService Queries { get; }

        public static async Task<ExplorerFixture> CreateAsync(CancellationToken cancellationToken)
        {
            return new ExplorerFixture(await SqliteTempDbFixture.CreateAsync(cancellationToken));
        }

        public ConnectionsPageViewModel CreateViewModel()
        {
            return new ConnectionsPageViewModel(
                Queries,
                Folders,
                ConnectionService,
                FolderService,
                Recent,
                Settings,
                SessionOpener,
                Notifier,
                _guids,
                Clock);
        }

        public async Task<Folder> AddFolderAsync(
            string name,
            Folder? parent = null,
            CancellationToken cancellationToken = default)
        {
            var folder = Folder.Create(_guids, name, parent, createdUtc: Clock.UtcNow).Value;
            await Folders.AddAsync(folder, cancellationToken);
            return folder;
        }

        public async Task<Connection> AddConnectionAsync(
            string name,
            Guid? folderId = null,
            EnvironmentKind environment = EnvironmentKind.Unspecified,
            bool isFavorite = false,
            string? colorOverrideHex = null,
            CancellationToken cancellationToken = default)
        {
            var connection = Connection.Create(_guids, name, $"{name.Replace(' ', '-').ToLowerInvariant()}.test", createdUtc: Clock.UtcNow).Value;
            _ = connection.SetDetails(
                null,
                AuthMethod.None,
                null,
                environment,
                colorOverrideHex,
                _guids,
                Clock.UtcNow);
            _ = connection.SetFolder(folderId, _guids, Clock.UtcNow)
                .SetFavorite(isFavorite, _guids, Clock.UtcNow);
            await Connections.AddAsync(connection, cancellationToken);
            return connection;
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class QueueSessionOpener : IConnectionSessionOpener
    {
        public Queue<bool> Results { get; } = [];

        public Task<bool> OpenAsync(
            Guid connectionId,
            ConnectionOpenMode mode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Results.Dequeue());
        }
    }
}
