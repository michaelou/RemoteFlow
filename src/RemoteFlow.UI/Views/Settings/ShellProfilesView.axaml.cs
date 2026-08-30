using Avalonia.Controls;
using Avalonia.Interactivity;
using RemoteFlow.UI.ViewModels.Settings;

namespace RemoteFlow.UI.Views.Settings;

public sealed partial class ShellProfilesView : UserControl
{
    public ShellProfilesView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellProfilesViewModel viewModel)
        {
            await viewModel.InitializeAsync().ConfigureAwait(true);
        }
    }

    // Duplicating and removing are the two actions inside the list that belong to the page rather than to
    // the profile: both change the collection the template is iterating, so they are reached from the
    // row's own DataContext rather than bound through an ancestor.
    //
    // Both sit inside the Expander's header, which is a toggle: the event is marked handled so pressing a
    // button acts on the profile without also folding the card it was pressed on.
    private void DuplicateProfile_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ShellProfileEditorViewModel profile } &&
            DataContext is ShellProfilesViewModel viewModel)
        {
            viewModel.DuplicateProfileCommand.Execute(profile);
            e.Handled = true;
        }
    }

    private void RemoveProfile_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ShellProfileEditorViewModel profile } &&
            DataContext is ShellProfilesViewModel viewModel)
        {
            viewModel.RemoveProfileCommand.Execute(profile);
            e.Handled = true;
        }
    }
}
