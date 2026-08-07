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
