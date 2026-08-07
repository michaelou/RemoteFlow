using RemoteFlow.UI.ViewModels;

namespace RemoteFlow.UI.Navigation;

public interface INavigationService
{
    event EventHandler? CurrentPageChanged;

    PageViewModel CurrentPage { get; }

    IReadOnlyList<NavigationItemViewModel> Items { get; }

    void Navigate(string pageKey);
}
