using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteFlow.UI.ViewModels;

public partial class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;
}

public sealed class TerminalsPageViewModel() : PageViewModel("Terminals");

public sealed class TransfersPageViewModel() : PageViewModel("Transfers");

public sealed class SettingsPageViewModel() : PageViewModel("Settings");
