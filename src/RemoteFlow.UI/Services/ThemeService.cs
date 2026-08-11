using Avalonia.Styling;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.UI.Services;

public interface IThemeService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SetThemeAsync(AppTheme theme, CancellationToken cancellationToken = default);
}

public sealed class ThemeService(global::Avalonia.Application application, ISettingsStore settingsStore) : IThemeService
{
    private readonly global::Avalonia.Application _application = application ?? throw new ArgumentNullException(nameof(application));
    private readonly ISettingsStore _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var theme = await _settingsStore.Get(SettingKeys.Theme, cancellationToken).ConfigureAwait(true);
        Apply(theme);
        // Before anything can render a terminal: the renderer reads its ANSI colours out of application
        // resources, and without them it falls back to a palette whose blue is unreadable on a dark surface.
        var schemeId = await _settingsStore.Get(SettingKeys.TerminalColorScheme, cancellationToken)
            .ConfigureAwait(true);
        TerminalPaletteResources.Apply(
            _application.Resources,
            ViewModels.Settings.TerminalColorSchemes.Resolve(schemeId));
    }

    public async Task SetThemeAsync(AppTheme theme, CancellationToken cancellationToken = default)
    {
        Apply(theme);
        await _settingsStore.Set(SettingKeys.Theme, theme, cancellationToken).ConfigureAwait(true);
    }

    private void Apply(AppTheme theme)
    {
        _application.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            AppTheme.System => ThemeVariant.Default,
            _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Unsupported theme."),
        };
    }
}
