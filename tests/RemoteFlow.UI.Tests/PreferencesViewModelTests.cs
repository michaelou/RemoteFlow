using RemoteFlow.Application.Abstractions;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Settings;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class PreferencesViewModelTests
{
    [Fact]
    public async Task AnUnconfiguredInstallShowsTenRecentAndTheDarkTheme()
    {
        var settings = new InMemorySettingsStore();
        var written = new List<string>();
        settings.SettingChanged += (_, e) => written.Add(e.Key);
        var viewModel = new PreferencesViewModel(settings);

        await viewModel.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, viewModel.RecentLimit);
        Assert.Equal(AppTheme.Dark, viewModel.SelectedTheme?.Value);
        // Opening the tab must not stamp the defaults into the store: that would turn a default that can
        // still move into a choice the user never made.
        Assert.Empty(written);
    }

    [Fact]
    public async Task TheRecentLimitClampsToTheRangeAndSurvivesARestart()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        var viewModel = new PreferencesViewModel(settings);
        await viewModel.InitializeAsync(token);

        viewModel.RecentLimit = 999;
        Assert.Equal(PreferencesViewModel.MaximumRecentLimit, viewModel.RecentLimit);
        viewModel.RecentLimit = -4;
        Assert.Equal(PreferencesViewModel.MinimumRecentLimit, viewModel.RecentLimit);
        viewModel.RecentLimit = 3;
        await viewModel.FlushAsync();

        Assert.Equal(3, await settings.Get(SettingKeys.RecentLimit, token));
        var restarted = new PreferencesViewModel(settings);
        await restarted.InitializeAsync(token);
        Assert.Equal(3, restarted.RecentLimit);
    }

    [Fact]
    public async Task ZeroSaysTheRecentListIsHiddenRatherThanEmpty()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        var viewModel = new PreferencesViewModel(settings);
        await viewModel.InitializeAsync(token);
        var described = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PreferencesViewModel.RecentLimitDescription))
            {
                described.Add(viewModel.RecentLimitDescription);
            }
        };

        viewModel.RecentLimit = 0;
        await viewModel.FlushAsync();

        Assert.Contains("hidden", viewModel.RecentLimitDescription, StringComparison.OrdinalIgnoreCase);
        _ = Assert.Single(described);
        Assert.Equal(0, await settings.Get(SettingKeys.RecentLimit, token));
    }

    [Fact]
    public async Task ChoosingAThemeAppliesItToTheRunningAppAndRemembersIt()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        var theme = new RecordingThemeService(settings);
        var viewModel = new PreferencesViewModel(settings, theme);
        await viewModel.InitializeAsync(token);

        Assert.True(viewModel.IsThemeAvailable);
        viewModel.SelectedTheme = viewModel.Themes.Single(option => option.Value == AppTheme.Light);
        await viewModel.FlushAsync();

        Assert.Equal([AppTheme.Light], theme.Applied);
        Assert.Equal(AppTheme.Light, await settings.Get(SettingKeys.Theme, token));
    }

    [Fact]
    public async Task WithoutAThemeServiceTheAppearanceSectionIsInert()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        var viewModel = new PreferencesViewModel(settings);
        await viewModel.InitializeAsync(token);

        viewModel.SelectedTheme = viewModel.Themes.Single(option => option.Value == AppTheme.Light);
        await viewModel.FlushAsync();

        Assert.False(viewModel.IsThemeAvailable);
        Assert.Equal(AppTheme.Dark, await settings.Get(SettingKeys.Theme, token));
    }

    private sealed class RecordingThemeService(InMemorySettingsStore settings) : IThemeService
    {
        public List<AppTheme> Applied { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public async Task SetThemeAsync(AppTheme theme, CancellationToken cancellationToken = default)
        {
            Applied.Add(theme);
            await settings.Set(SettingKeys.Theme, theme, cancellationToken);
        }
    }
}
