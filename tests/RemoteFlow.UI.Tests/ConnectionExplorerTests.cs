using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
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
    public async Task StorageConnectionsAppearInTheListTheSearchBoxAndTheFilterChips()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        var bucket = await fixture.AddConnectionAsync(
            "Archive bucket",
            protocol: ProtocolType.S3,
            cancellationToken: token);
        var container = await fixture.AddConnectionAsync(
            "Archive container",
            protocol: ProtocolType.AzureBlob,
            cancellationToken: token);
        _ = await fixture.AddConnectionAsync("Shell", protocol: ProtocolType.Ssh, cancellationToken: token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);

        // The chips read as product names, not as upper-cased enum members.
        Assert.Equal(
            ["SSH", "SFTP", "RDP", "S3", "Azure Blob"],
            [.. viewModel.ProtocolFilters.Select(chip => chip.Label)]);

        // A bucket and a container are drawn differently: they used to share the storage glyph, and colour
        // alone is not a difference for everyone. The icon switch throws on an unmapped protocol, so this
        // is also what proves the new members reached it.
        var nodes = RealConnections(viewModel);
        Assert.Equal("Icon.Storage", nodes.Single(node => node.Name == "Archive bucket").IconKey);
        Assert.Equal("Icon.Cloud", nodes.Single(node => node.Name == "Archive container").IconKey);
        Assert.Equal("Icon.Terminals", nodes.Single(node => node.Name == "Shell").IconKey);

        viewModel.SearchText = "Archive";
        await viewModel.SearchChangesSettled;

        Assert.Equal(
            [bucket.Id, container.Id],
            [.. RealConnections(viewModel).Select(node => node.Id!.Value).Order()]);

        viewModel.SearchText = null;
        viewModel.ProtocolFilters.Single(chip => chip.Protocol == ProtocolType.AzureBlob).IsSelected = true;
        await viewModel.SearchChangesSettled;

        Assert.Equal(container.Id, Assert.Single(RealConnections(viewModel)).Id);
        Assert.Contains("Azure Blob", viewModel.ActiveFilterSummary, StringComparison.Ordinal);
    }

    /// <summary>A colour override moves the connection's session tab, not its chip. The chip is the one
    /// place the environment is stated plainly, so it goes on saying PROD in production's colour however
    /// the connection has been painted everywhere else.</summary>
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
        Assert.Equal("prod", node.Badge.Tag);
    }

    [Fact]
    public async Task NewFolderPicksAFreeNamePerParentAndOpensItForRenaming()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);
        ExplorerNodeViewModel? renaming = null;
        viewModel.RenameStarted += (_, node) => renaming = node;

        var first = await viewModel.CreateFolderAsync(cancellationToken: token);
        var second = await viewModel.CreateFolderAsync(cancellationToken: token);
        var child = await viewModel.CreateFolderAsync(first, token);

        var folders = await fixture.Folders.ListAsync(token);
        Assert.Equal(3, folders.Count);
        Assert.Equal("New folder", folders.Single(folder => folder.Id == first!.Value).Name);
        Assert.Equal("New folder 2", folders.Single(folder => folder.Id == second!.Value).Name);
        var childFolder = folders.Single(folder => folder.Id == child!.Value);
        Assert.Equal(first, childFolder.ParentId);
        Assert.Equal("/New folder/New folder", childFolder.Path);
        Assert.True(FindFolder(viewModel, first!.Value).IsExpanded);
        Assert.Equal(child, renaming!.Id);
        Assert.True(renaming.IsRenaming);
        Assert.False(viewModel.IsEmpty);
    }

    [Fact]
    public async Task FolderRenameAndDeletePersistAndReparentTheContents()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        var folder = await fixture.AddFolderAsync("Staging", cancellationToken: token);
        var connection = await fixture.AddConnectionAsync("Box", folder.Id, cancellationToken: token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);
        var node = FindFolder(viewModel, folder.Id);

        node.BeginRenameCommand.Execute(null);
        node.EditName = "Production";
        await node.CommitRenameCommand.ExecuteAsync(null);

        Assert.False(node.IsRenaming);
        Assert.Equal("/Production", (await fixture.Folders.GetByIdAsync(folder.Id, token))!.Path);

        await FindFolder(viewModel, folder.Id).DeleteCommand.ExecuteAsync(null);

        Assert.Null(await fixture.Folders.GetByIdAsync(folder.Id, token));
        Assert.Null((await fixture.Connections.GetByIdAsync(connection.Id, token))!.FolderId);
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
    public async Task AReloadRebuildsTheTreeAndTheTagChipsFromTheImportedData()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        var beforeTag = await fixture.AddTagAsync("Before", token);
        var before = await fixture.AddConnectionAsync("Before", cancellationToken: token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);
        viewModel.TagFilters.Single(chip => chip.TagId == beforeTag.Id).IsSelected = true;
        await viewModel.SearchChangesSettled;

        // Stand in for the import: the rows change without any repository raising a per-connection change.
        await fixture.Connections.DeleteAsync(before.Id, token);
        await fixture.Tags.DeleteAsync(beforeTag.Id, token);
        var afterTag = await fixture.AddTagAsync("After", token);
        var after = await fixture.AddConnectionAsync("After", cancellationToken: token);
        fixture.Notifier.NotifyReloaded();
        await viewModel.ConnectionChangesSettled;

        Assert.Equal(after.Id, Assert.Single(RealConnections(viewModel)).Id);
        Assert.Equal([afterTag.Id], [.. viewModel.TagFilters.Select(chip => chip.TagId)]);
        Assert.DoesNotContain(viewModel.TagFilters, chip => chip.IsSelected);
        Assert.False(viewModel.HasActiveFilters);
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

    /// <summary>Each protocol paints its glyph in its own colour, and the nodes that have no protocol keep
    /// inheriting the row's foreground. The second half is the part that would break silently: a rule that
    /// matched every glyph would blank the folders or freeze them at one colour.</summary>
    [AvaloniaFact]
    public async Task EachProtocolPaintsItsGlyphAndAFolderKeepsInheritingTheRow()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        var folder = await fixture.AddFolderAsync("Servers", cancellationToken: token);
        foreach (var (name, protocol) in new[]
        {
            ("Shell", ProtocolType.Ssh),
            ("Files", ProtocolType.Sftp),
            ("Desktop", ProtocolType.Rdp),
            ("Bucket", ProtocolType.S3),
            ("Container", ProtocolType.AzureBlob),
        })
        {
            _ = await fixture.AddConnectionAsync(name, protocol: protocol, cancellationToken: token);
        }

        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);
        await viewModel.SetExpandedAsync(FindFolder(viewModel, folder.Id), true, token);
        var view = new ConnectionsView { DataContext = viewModel };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Shell"] = "Brush.Protocol.Ssh",
            ["Files"] = "Brush.Protocol.Sftp",
            ["Desktop"] = "Brush.Protocol.Rdp",
            ["Bucket"] = "Brush.Protocol.S3",
            ["Container"] = "Brush.Protocol.AzureBlob",
        };
        var painted = expected.Keys
            .Select(name => RowGlyph(window, name).Foreground)
            .ToArray();

        foreach (var (name, key) in expected)
        {
            var glyph = RowGlyph(window, name);
            Assert.Equal(Brush(window, key), glyph.Foreground);
            // Geometry that fails to parse resolves to nothing and draws nothing, silently.
            Assert.NotNull(glyph.Data);
        }

        // Five protocols, five colours: a shared brush would make two of them indistinguishable.
        Assert.Equal(5, painted.Distinct().Count());

        // The folder names no brush, so its glyph still answers to the row rather than to nothing.
        Assert.NotNull(RowGlyph(window, "Servers").Foreground);
        window.Close();
    }

    /// <summary>The two toolbar toggles: one closes and reopens every folder and persists what it did, and
    /// one drops the host line off the rows and is remembered for next time.</summary>
    [AvaloniaFact]
    public async Task TheToolbarCollapsesEveryFolderAndHidesTheHostLineAndRemembersBoth()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        var outer = await fixture.AddFolderAsync("Outer", cancellationToken: token);
        var inner = await fixture.AddFolderAsync("Inner", outer, token);
        _ = await fixture.AddConnectionAsync("Shell", inner.Id, cancellationToken: token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);

        Assert.False(viewModel.HasExpandedFolders);
        Assert.True(viewModel.HasFolders);
        Assert.Equal("Icon.ExpandAll", viewModel.ExpandCollapseIconKey);

        // Open everything, including the folder nested inside the closed one.
        await viewModel.ToggleAllFoldersAsync(token);

        Assert.True(viewModel.HasExpandedFolders);
        Assert.Equal("Icon.CollapseAll", viewModel.ExpandCollapseIconKey);
        Assert.True((await fixture.Folders.GetByIdAsync(inner.Id, token))!.IsExpanded);

        await viewModel.ToggleAllFoldersAsync(token);

        Assert.False(viewModel.HasExpandedFolders);
        Assert.False((await fixture.Folders.GetByIdAsync(outer.Id, token))!.IsExpanded);
        Assert.False((await fixture.Folders.GetByIdAsync(inner.Id, token))!.IsExpanded);

        Assert.True(viewModel.ShowSecondaryText);
        Assert.Equal("Icon.DetailLine", viewModel.SecondaryTextIconKey);

        await viewModel.ToggleSecondaryTextAsync(token);

        Assert.False(viewModel.ShowSecondaryText);
        Assert.Equal("Icon.DetailLineOff", viewModel.SecondaryTextIconKey);
        Assert.False(await fixture.Settings.Get(SettingKeys.ShowConnectionDetailLine, token));

        // A page opened later starts where this one was left.
        using var reopened = fixture.CreateViewModel();
        await reopened.InitializeAsync(token);

        Assert.False(reopened.ShowSecondaryText);
    }

    /// <summary>The toolbar's glyph buttons: every one of them draws a glyph, and every one says what it is
    /// twice over — a tooltip for the pointer and an automation name for a screen reader. An icon button
    /// that resolved no geometry would be an invisible, unlabelled square, which is a thing that renders
    /// perfectly and tells nobody anything.</summary>
    [AvaloniaFact]
    public async Task EveryGlyphButtonInTheToolbarDrawsAGlyphAndNamesItself()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        _ = await fixture.AddFolderAsync("Servers", cancellationToken: token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);
        var view = new ConnectionsView { DataContext = viewModel };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        foreach (var name in new[]
        {
            "New folder",
            "Clear recent",
            "Expand all folders",
            "Hide the host line under each name",
        })
        {
            var button = GlyphButton(window, name);
            var glyph = Assert.IsType<PathIcon>(button.Content);
            Assert.NotNull(glyph.Data);
            Assert.False(
                string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string),
                $"the {name} button has no tooltip.");
        }

        // The two toggles say which way they point, in the glyph as well as in the words.
        var folders = GlyphButton(window, "Expand all folders");
        var before = ((PathIcon)folders.Content!).Data;
        await viewModel.ToggleAllFoldersAsync(token);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.NotEqual(before, ((PathIcon)GlyphButton(window, "Collapse all folders").Content!).Data);
        window.Close();
    }

    /// <summary>Hiding the host line has to close the row up rather than leave the gap it was in. The point
    /// of the toggle is the rows it buys back, so the height a two-line row reserves has to go with it.
    /// </summary>
    [AvaloniaFact]
    public async Task HidingTheHostLineShortensTheRowItWasOn()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await ExplorerFixture.CreateAsync(token);
        _ = await fixture.AddConnectionAsync("Shell", cancellationToken: token);
        using var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync(token);
        var view = new ConnectionsView { DataContext = viewModel };
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var tall = RowGlyph(window, "Shell").FindAncestorOfType<TreeViewItem>()!.Bounds.Height;

        await viewModel.ToggleSecondaryTextAsync(token);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var short_ = RowGlyph(window, "Shell").FindAncestorOfType<TreeViewItem>()!.Bounds.Height;

        Assert.True(
            short_ <= tall * 0.75,
            $"the row barely closed up: {tall} before, {short_} after.");
        window.Close();
    }

    /// <summary>Resolved against the window's own theme variant: the protocol colours live in the theme
    /// dictionaries, and the application-level lookup does not carry a variant to match them with.</summary>
    private static IBrush Brush(Window window, string key)
    {
        Assert.True(window.TryFindResource(key, window.ActualThemeVariant, out var value), $"{key} is missing.");
        return Assert.IsAssignableFrom<IBrush>(value);
    }

    private static Button GlyphButton(Window window, string automationName)
    {
        return window.GetVisualDescendants()
            .OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == automationName);
    }

    /// <summary>The one glyph drawn on the row for the named node.</summary>
    private static PathIcon RowGlyph(Window window, string name)
    {
        return window.GetVisualDescendants()
            .OfType<PathIcon>()
            .Single(icon => icon.DataContext is ExplorerNodeViewModel node && node.Name == name);
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

        public async Task<ConnectionOpenResult> OpenAsync(
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
            return result ? ConnectionOpenResult.Success() : ConnectionOpenResult.Failure();
        }
    }
}
