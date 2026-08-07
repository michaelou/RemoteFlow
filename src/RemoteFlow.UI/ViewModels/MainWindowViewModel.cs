using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.UI.Navigation;

namespace RemoteFlow.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;

    public MainWindowViewModel(INavigationService navigationService)
    {
        _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
        CurrentPage = navigationService.CurrentPage;
        navigationService.CurrentPageChanged += (_, _) => CurrentPage = navigationService.CurrentPage;
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems => _navigationService.Items;

    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; private set; }

    public void Navigate(NavigationItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _navigationService.Navigate(item.Key);
    }
}
