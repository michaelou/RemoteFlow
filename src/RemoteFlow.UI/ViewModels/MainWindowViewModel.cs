using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.ViewModels.CommandPalette;
using RemoteFlow.UI.ViewModels.Terminal;
using RemoteFlow.UI.ViewModels.Transfers;

namespace RemoteFlow.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>The sidebar with its labels showing, and collapsed to a rail of icons. Fixed rather than
    /// draggable: the rail is exactly wide enough to centre a 16-pixel glyph under an 8-pixel list padding,
    /// and there is nothing in between the two states worth stopping at.</summary>
    private const double _expandedWidth = 220;
    private const double _collapsedWidth = 56;

    private readonly INavigationService _navigationService;
    private readonly ISettingsStore? _settings;

    public MainWindowViewModel(INavigationService navigationService)
        : this(navigationService, new CommandPaletteViewModel(), null, null)
    {
    }

    public MainWindowViewModel(
        INavigationService navigationService,
        CommandPaletteViewModel commandPalette)
        : this(navigationService, commandPalette, null, null)
    {
    }

    public MainWindowViewModel(
        INavigationService navigationService,
        CommandPaletteViewModel commandPalette,
        TerminalsPageViewModel? terminals)
        : this(navigationService, commandPalette, terminals, null)
    {
    }

    public MainWindowViewModel(
        INavigationService navigationService,
        CommandPaletteViewModel commandPalette,
        TerminalsPageViewModel? terminals,
        TransfersPageViewModel? transfers)
        : this(navigationService, commandPalette, terminals, transfers, null)
    {
    }

    public MainWindowViewModel(
        INavigationService navigationService,
        CommandPaletteViewModel commandPalette,
        TerminalsPageViewModel? terminals,
        TransfersPageViewModel? transfers,
        ISettingsStore? settings)
    {
        _settings = settings;
        // Expanded until the stored preference says otherwise: the first frame is drawn before
        // InitializeAsync has read anything, and a sidebar that flashes shut on every start is worse than
        // one that opens a moment before it collapses.
        IsNavigationExpanded = SettingKeys.NavigationExpanded.DefaultValue;
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        Palette = commandPalette ?? throw new ArgumentNullException(nameof(commandPalette));
        Terminals = terminals;
        Transfers = transfers;
        CurrentPage = navigationService.CurrentPage;
        navigationService.CurrentPageChanged += (_, _) => CurrentPage = navigationService.CurrentPage;
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems => _navigationService.Items;

    /// <summary>The sidebar entry for the page on screen, including pages opened from elsewhere
    /// (connecting from the explorer, the command palette) rather than from the sidebar.</summary>
    public NavigationItemViewModel? CurrentNavigationItem => _navigationService.Items
        .FirstOrDefault(item => string.Equals(item.Key, _navigationService.CurrentPageKey, StringComparison.Ordinal));

    public CommandPaletteViewModel Palette { get; }

    public TerminalsPageViewModel? Terminals { get; }

    public TransfersPageViewModel? Transfers { get; }

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; private set; }

    /// <summary>Whether the sidebar shows its labels. Collapsed, the rows keep their icons and grow a
    /// tooltip, so the sidebar is still navigable by pointer without reading a word.</summary>
    [ObservableProperty]
    public partial bool IsNavigationExpanded { get; set; }

    public double NavigationWidth => IsNavigationExpanded ? _expandedWidth : _collapsedWidth;

    /// <summary>One string for the toggle's tooltip and its accessible name: the two must not be allowed
    /// to say different things about the same button.</summary>
    public string NavigationToggleLabel => IsNavigationExpanded ? "Collapse sidebar" : "Expand sidebar";

    public string NavigationToggleIconKey => IsNavigationExpanded
        ? "Icon.CollapseSidebar"
        : "Icon.ExpandSidebar";

    /// <summary>Set when the toggle writes, so a test can wait for the write instead of racing it.</summary>
    public Task NavigationChangesSettled { get; private set; } = Task.CompletedTask;

    /// <summary>Restores the sidebar to whichever state it was left in. Read only — the toggle is the
    /// only thing that writes, so starting up cannot overwrite the preference with its own default.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_settings is null)
        {
            return;
        }

        IsNavigationExpanded = await _settings
            .Get(SettingKeys.NavigationExpanded, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>Collapses the sidebar, or opens it again, and remembers which. A failed write leaves the
    /// sidebar where the user just put it: the click did what was asked, and only the remembering failed.
    /// </summary>
    public async Task ToggleNavigationAsync(CancellationToken cancellationToken = default)
    {
        IsNavigationExpanded = !IsNavigationExpanded;
        if (_settings is null)
        {
            return;
        }

        try
        {
            await _settings
                .Set(SettingKeys.NavigationExpanded, IsNavigationExpanded, cancellationToken)
                .ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Nowhere on the shell to report this, and nothing is lost but the remembering: the sidebar
            // is in the state that was asked for, and the next toggle will try to write again.
        }
    }

    public void RequestToggleNavigation()
    {
        NavigationChangesSettled = ToggleNavigationAsync();
    }

    public void Navigate(NavigationItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _navigationService.Navigate(item.Key);
    }

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        OnPropertyChanged(nameof(CurrentNavigationItem));
    }

    partial void OnIsNavigationExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(NavigationWidth));
        OnPropertyChanged(nameof(NavigationToggleLabel));
        OnPropertyChanged(nameof(NavigationToggleIconKey));
    }
}
