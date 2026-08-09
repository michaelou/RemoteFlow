using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteFlow.UI.ViewModels;

public partial class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;
}

public sealed class SettingsPageViewModel(
    Settings.TerminalSettingsViewModel? terminal = null,
    Security.TrustedKeysViewModel? security = null,
    Settings.AboutViewModel? about = null,
    Settings.RdpSettingsViewModel? rdp = null) : PageViewModel("Settings")
{
    public Settings.TerminalSettingsViewModel? Terminal { get; } = terminal;

    public Security.TrustedKeysViewModel? Security { get; } = security;

    public Settings.AboutViewModel? About { get; } = about;

    public Settings.RdpSettingsViewModel? Rdp { get; } = rdp;
}
