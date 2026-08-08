using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.ViewModels.CommandPalette;
using RemoteFlow.UI.ViewModels.Terminal;
using RemoteFlow.UI.ViewModels.Transfers;

namespace RemoteFlow.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;

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
    {
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

    public void Navigate(NavigationItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _navigationService.Navigate(item.Key);
    }

    partial void OnCurrentPageChanged(PageViewModel value)
    {
        OnPropertyChanged(nameof(CurrentNavigationItem));
    }
}
