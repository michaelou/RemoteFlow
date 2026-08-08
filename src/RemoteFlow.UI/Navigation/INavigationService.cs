using RemoteFlow.UI.ViewModels;

namespace RemoteFlow.UI.Navigation;

public interface INavigationService
{
    event EventHandler? CurrentPageChanged;

    PageViewModel CurrentPage { get; }

    /// <summary>The registration key of <see cref="CurrentPage"/>, so the sidebar can follow navigation
    /// that was started from somewhere other than the sidebar itself.</summary>
    string CurrentPageKey { get; }

    IReadOnlyList<NavigationItemViewModel> Items { get; }

    void Navigate(string pageKey);
}
