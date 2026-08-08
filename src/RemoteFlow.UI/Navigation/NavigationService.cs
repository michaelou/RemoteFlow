using RemoteFlow.UI.ViewModels;

namespace RemoteFlow.UI.Navigation;

public sealed record NavigationPageRegistration(string Key, string Title, string IconKey, Func<PageViewModel> Factory);

public sealed class NavigationService : INavigationService
{
    private readonly Dictionary<string, NavigationPageRegistration> _registry;
    private readonly Dictionary<string, PageViewModel> _pages = new(StringComparer.Ordinal);

    public NavigationService(IEnumerable<NavigationPageRegistration> registrations, string initialPageKey)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentException.ThrowIfNullOrWhiteSpace(initialPageKey);
        var registrationArray = registrations.ToArray();
        _registry = registrationArray.ToDictionary(item => item.Key, StringComparer.Ordinal);
        Items = [.. registrationArray.Select(item => new NavigationItemViewModel(item.Key, item.Title, item.IconKey))];
        CurrentPage = Resolve(initialPageKey);
        CurrentPageKey = initialPageKey;
    }

    public event EventHandler? CurrentPageChanged;

    public PageViewModel CurrentPage { get; private set; }

    public string CurrentPageKey { get; private set; }

    public IReadOnlyList<NavigationItemViewModel> Items { get; }

    public static NavigationService CreateDefault()
    {
        return new NavigationService(
        [
            new("connections", "Connections", "Icon.Connections", () => new PageViewModel("Connections")),
            new("terminals", "Terminals", "Icon.Terminals", () => new PageViewModel("Terminals")),
            new("transfers", "Transfers", "Icon.Transfers", () => new PageViewModel("Transfers")),
            new("settings", "Settings", "Icon.Settings", () => new PageViewModel("Settings")),
        ],
        "connections");
    }

    public void Navigate(string pageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageKey);
        var page = Resolve(pageKey);
        if (ReferenceEquals(page, CurrentPage))
        {
            return;
        }

        CurrentPage = page;
        CurrentPageKey = pageKey;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    private PageViewModel Resolve(string pageKey)
    {
        if (!_registry.TryGetValue(pageKey, out var registration))
        {
            throw new KeyNotFoundException($"No page is registered with the key '{pageKey}'.");
        }

        if (!_pages.TryGetValue(pageKey, out var page))
        {
            page = registration.Factory();
            _pages.Add(pageKey, page);
        }

        return page;
    }
}
