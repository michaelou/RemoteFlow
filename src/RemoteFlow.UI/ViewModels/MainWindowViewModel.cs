using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.ViewModels.CommandPalette;

namespace RemoteFlow.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;

    public MainWindowViewModel(INavigationService navigationService)
        : this(navigationService, new CommandPaletteViewModel())
    {
    }

    public MainWindowViewModel(
        INavigationService navigationService,
        CommandPaletteViewModel commandPalette)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        Palette = commandPalette ?? throw new ArgumentNullException(nameof(commandPalette));
        CurrentPage = navigationService.CurrentPage;
        navigationService.CurrentPageChanged += (_, _) => CurrentPage = navigationService.CurrentPage;
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems => _navigationService.Items;

    public CommandPaletteViewModel Palette { get; }

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; private set; }

    public void Navigate(NavigationItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _navigationService.Navigate(item.Key);
    }
}
