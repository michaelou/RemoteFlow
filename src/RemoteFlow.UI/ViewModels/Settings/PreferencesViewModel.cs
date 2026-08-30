using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Settings;

/// <summary>The Preferences tab: the choices that are about the app itself rather than about one
/// protocol. Each one is written as it is made — there is no Save button on this page, so a value that
/// looks set is set.</summary>
public sealed partial class PreferencesViewModel(ISettingsStore settings, IThemeService? theme = null)
    : ObservableObject
{
    public const int MinimumRecentLimit = 0;
    public const int MaximumRecentLimit = 50;

    private readonly ISettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly IThemeService? _theme = theme;
    private bool _initialized;
    private Task _pendingSave = Task.CompletedTask;
#pragma warning disable IDE0032 // The setter clamps before it notifies, so the field cannot be auto-generated.
    private int _recentLimit = SettingKeys.RecentLimit.DefaultValue;
#pragma warning restore IDE0032

    /// <summary>Whether the appearance section can do anything. A host that runs the view models without an
    /// Avalonia application — the tests, mainly — has no theme service to switch, and the section is shown
    /// disabled rather than hidden so it still says what the app can do.</summary>
    public bool IsThemeAvailable => _theme is not null;

    public IReadOnlyList<AppThemeOption> Themes { get; } =
    [
        new(AppTheme.Dark, "Dark", "The default: light text on a dark surface."),
        new(AppTheme.Light, "Light", "Dark text on a light surface."),
    ];

    [ObservableProperty]
    public partial AppThemeOption? SelectedTheme { get; set; }

    /// <summary>How many connections the explorer lists under Recent. Zero hides the heading entirely.
    /// </summary>
    public int RecentLimit
    {
        get => _recentLimit;
        set
        {
            if (SetProperty(ref _recentLimit, Math.Clamp(value, MinimumRecentLimit, MaximumRecentLimit)))
            {
                OnPropertyChanged(nameof(RecentLimitDescription));
                if (_initialized)
                {
                    _pendingSave = SaveRecentLimitAsync(_pendingSave, _recentLimit);
                }
            }
        }
    }

    public string RecentLimitDescription => _recentLimit == 0
        ? "Recent is hidden. History is still recorded, so raising this shows what was opened meanwhile."
        : $"The explorer lists the {_recentLimit} most recently opened connections under Recent.";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        RecentLimit = await _settings.Get(SettingKeys.RecentLimit, cancellationToken).ConfigureAwait(true);
        var theme = await _settings.Get(SettingKeys.Theme, cancellationToken).ConfigureAwait(true);
        SelectedTheme = Themes.FirstOrDefault(option => option.Value == theme) ?? Themes[0];
        _initialized = true;
    }

    public async Task FlushAsync()
    {
        await _pendingSave.ConfigureAwait(false);
    }

    partial void OnSelectedThemeChanged(AppThemeOption? value)
    {
        if (_initialized && value is not null && _theme is not null)
        {
            _pendingSave = SaveThemeAsync(_pendingSave, value.Value);
        }
    }

    private async Task SaveRecentLimitAsync(Task previousSave, int value)
    {
        await previousSave.ConfigureAwait(true);
        await _settings.Set(SettingKeys.RecentLimit, value).ConfigureAwait(true);
    }

    // The theme service applies the variant and stores it in one step, so the running window changes with
    // the setting rather than at the next start.
    private async Task SaveThemeAsync(Task previousSave, AppTheme value)
    {
        await previousSave.ConfigureAwait(true);
        await _theme!.SetThemeAsync(value).ConfigureAwait(true);
    }
}

public sealed record AppThemeOption(AppTheme Value, string DisplayName, string Description);
