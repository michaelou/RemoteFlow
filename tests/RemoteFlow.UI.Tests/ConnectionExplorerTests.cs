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
    [Fact]
    public async Task SearchDebouncesAndAppliesEveryLatestKeystroke()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        var alpha = await fixture.AddConnectionAsync("Alpha", cancellationToken: token);
        var beta = await fixture.AddConnectionAsync("Beta", cancellationToken: token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);

        viewModel.SearchText = "alp";
        Assert.NotNull(FindRealConnection(viewModel, alpha.Id));
        Assert.NotNull(FindRealConnection(viewModel, beta.Id));
        viewModel.SearchText = "b";
        viewModel.SearchText = "be";
        viewModel.SearchText = "beta";
        await viewModel.SearchChangesSettled;

        Assert.Equal(beta.Id, Assert.Single(RealConnections(viewModel)).Id);
        Assert.Equal("Text: beta", viewModel.ActiveFilterSummary);
    }

    [Fact]
    public async Task ChipsComposeWithTextAndClearAllResetsEverythingAtOnce()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        var critical = await fixture.AddTagAsync("Critical", token);
        var match = await fixture.AddConnectionAsync(
            "Needle production",
            environment: EnvironmentKind.Production,
            isFavorite: true,
            protocol: ProtocolType.Ssh,
            cancellationToken: token);
        _ = await fixture.Connections.AddTagAsync(match.Id, critical.Id, token);
        _ = await fixture.AddConnectionAsync(
            "Needle staging",
            environment: EnvironmentKind.Staging,
            isFavorite: true,
            protocol: ProtocolType.Ssh,
            cancellationToken: token);
        _ = await fixture.AddConnectionAsync(
            "Other production",
            environment: EnvironmentKind.Production,
            isFavorite: true,
            protocol: ProtocolType.Ssh,
            cancellationToken: token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);

        viewModel.SearchText = "Needle";
        viewModel.ProtocolFilters.Single(chip => chip.Protocol == ProtocolType.Ssh).IsSelected = true;
        viewModel.EnvironmentFilters.Single(chip => chip.Environment == EnvironmentKind.Production).IsSelected = true;
        viewModel.TagFilters.Single(chip => chip.TagId == critical.Id).IsSelected = true;
        viewModel.FavoritesOnly = true;
        await viewModel.SearchChangesSettled;

        Assert.Equal(match.Id, Assert.Single(RealConnections(viewModel)).Id);
        Assert.True(viewModel.HasActiveFilters);

        viewModel.ClearAllFilters();
        await viewModel.SearchChangesSettled;

        Assert.Equal(3, RealConnections(viewModel).Count);
        Assert.False(viewModel.HasActiveFilters);
        Assert.True(string.IsNullOrEmpty(viewModel.SearchText));
        Assert.DoesNotContain(
            viewModel.ProtocolFilters.Concat(viewModel.EnvironmentFilters).Concat(viewModel.TagFilters),
            chip => chip.IsSelected);
    }

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

    private static IReadOnlyList<ExplorerNodeViewModel> RealConnections(ConnectionsPageViewModel viewModel)
    {
        return [.. viewModel.RootNodes
            .Where(node => !node.IsVirtual)
            .SelectMany(node => Flatten([node]))
            .Where(node => node.Kind == ExplorerNodeKind.Connection)];
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
            Notifier = new ConnectionChangeNotifier();
            Connections = new ConnectionRepository(database.Factory);
            Folders = new FolderRepository(database.Factory);
            Tags = new TagRepository(database.Factory);
            Recent = new RecentConnectionStore(database.Factory);
            SessionOpener = new QueueSessionOpener((id, token) =>
                Recent.RecordOpenedAsync(id, Clock.UtcNow, token));
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

        public TagRepository Tags { get; }

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
                Tags,
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
            ProtocolType protocol = ProtocolType.Ssh,
            CancellationToken cancellationToken = default)
        {
            var connection = Connection.Create(
                _guids,
                name,
                $"{name.Replace(' ', '-').ToLowerInvariant()}.test",
                protocol,
                Clock.UtcNow).Value;
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

        public async Task<Tag> AddTagAsync(string name, CancellationToken cancellationToken = default)
        {
            var tag = Tag.Create(_guids, name, createdUtc: Clock.UtcNow).Value;
            await Tags.AddAsync(tag, cancellationToken);
            return tag;
        }

        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }

    private sealed class QueueSessionOpener(
        Func<Guid, CancellationToken, Task>? recordSuccess = null) : IConnectionSessionOpener
    {
        public Queue<bool> Results { get; } = [];

        public async Task<bool> OpenAsync(
            Guid connectionId,
            ConnectionOpenMode mode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Results.Dequeue();
            if (result && recordSuccess is not null)
            {
                await recordSuccess(connectionId, cancellationToken);
            }
            return result;
        }
    }
}
