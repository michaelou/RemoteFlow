namespace RemoteFlow.UI.ViewModels;

/// <summary><paramref name="IconKey" /> names a <c>StreamGeometry</c> in Styles/Icons.axaml; the shell
/// resolves it at render time so the glyphs stay in XAML rather than in the view model.</summary>
public sealed record NavigationItemViewModel(string Key, string Title, string IconKey);
