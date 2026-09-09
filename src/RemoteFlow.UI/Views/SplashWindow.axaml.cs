using Avalonia.Controls;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;

namespace RemoteFlow.UI.Views;

/// <summary>The window shown while the application starts, so the first thing on screen is never an
/// empty frame waiting for a database migration to finish.</summary>
public sealed partial class SplashWindow : Window
{
    public SplashWindow()
        : this(new SplashViewModel())
    {
    }

    public SplashWindow(SplashViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = viewModel;
    }

    public SplashViewModel ViewModel { get; }

    /// <summary>Where startup reports what it is doing.</summary>
    public IStartupProgress Progress => ViewModel;
}
