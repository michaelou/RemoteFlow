using CommunityToolkit.Mvvm.ComponentModel;

namespace RemoteFlow.UI.ViewModels;

public sealed partial class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;
}
