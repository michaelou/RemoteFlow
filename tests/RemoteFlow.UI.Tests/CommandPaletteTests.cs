using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Queries;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.CommandPalette;
using RemoteFlow.UI.Views;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class CommandPaletteTests
{
    [Fact]
    public async Task ResultsDisambiguateAndEnterConnectsAndRecordsRecency()
    {
        var token = TestContext.Current.CancellationToken;
        var first = Item(Guid.NewGuid(), "Web", "web-one.test", "/Prod/EU");
        var second = Item(Guid.NewGuid(), "Web", "web-two.test", "/Staging");
        var queries = new StubQueries([first, second]);
        var recent = new RecordingRecentStore();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var opener = new RecordingOpener(recent, clock);
        using var viewModel = new CommandPaletteViewModel(queries, opener, recent, clock);
        viewModel.Open();

        viewModel.SearchText = "web";
        await viewModel.SearchChangesSettled;

        Assert.Equal(2, viewModel.Results.Count);
        Assert.Equal("/Prod/EU • web-one.test:22", viewModel.Results[0].Description);
        Assert.Equal("/Staging • web-two.test:22", viewModel.Results[1].Description);
        Assert.True(await viewModel.ConnectSelectedAsync(token));
        Assert.Equal(first.Id, opener.ConnectionId);
        Assert.Equal((first.Id, clock.UtcNow), recent.Recorded);
        Assert.False(viewModel.IsOpen);
    }

    [Fact]
    public async Task NoMatchesShowsAHelpfulState()
    {
        using var viewModel = new CommandPaletteViewModel(
            new StubQueries([]),
            new RecordingOpener(),
            new RecordingRecentStore(),
            new FakeClock(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero)));
        viewModel.Open();

        viewModel.SearchText = "missing";
        await viewModel.SearchChangesSettled;

        Assert.True(viewModel.HasEmptyState);
        Assert.Contains("No connections match", viewModel.EmptyMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task ControlKOpensOverAnyPageAndEscapeCloses()
    {
        var navigation = NavigationService.CreateDefault();
        navigation.Navigate("terminals");
        var palette = new CommandPaletteViewModel();
        var window = new MainWindow(
            new MainWindowViewModel(navigation, palette),
            new WindowGeometryService(new InMemorySettingsStore()));
        window.Show();
        var navigationList = Assert.IsType<ListBox>(window.FindControl<ListBox>("NavigationList"));
        _ = navigationList.Focus();

        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.K,
            PhysicalKey = PhysicalKey.K,
            KeyModifiers = KeyModifiers.Control,
        });

        Assert.True(palette.IsOpen);
        Assert.Equal("Terminals", navigation.CurrentPage.Title);

        window.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Escape,
            PhysicalKey = PhysicalKey.Escape,
        });
        await Dispatcher.UIThread.InvokeAsync(() => { });

        Assert.False(palette.IsOpen);
        window.Close();
    }

    private static ConnectionListItem Item(Guid id, string name, string host, string folderPath)
    {
        return new ConnectionListItem(
            id,
            name,
            host,
            22,
            ProtocolType.Ssh,
            EnvironmentKind.Production,
            false,
            null,
            folderPath,
            null,
            null,
            [],
            null);
    }

    private sealed class StubQueries(IReadOnlyList<ConnectionListItem> results) : IConnectionQueryService
    {
        public Task<IReadOnlyList<ConnectionListItem>> QueryAsync(
            ConnectionFilter filter,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<ConnectionListItem>> SearchPaletteAsync(
            string text,
            int limit = 20,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ConnectionListItem>>([.. results.Take(limit)]);
        }
    }

    private sealed class RecordingOpener(
        IRecentConnectionStore? recent = null,
        IClock? clock = null) : IConnectionSessionOpener
    {
        public Guid? ConnectionId { get; private set; }

        public async Task<bool> OpenAsync(
            Guid connectionId,
            ConnectionOpenMode mode,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectionId = connectionId;
            if (recent is not null && clock is not null)
            {
                await recent.RecordOpenedAsync(connectionId, clock.UtcNow, cancellationToken);
            }
            return true;
        }
    }

    private sealed class RecordingRecentStore : IRecentConnectionStore
    {
        public (Guid Id, DateTimeOffset Time)? Recorded { get; private set; }

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
            Recorded = (connectionId, openedUtc);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid connectionId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
