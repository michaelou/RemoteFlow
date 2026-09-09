using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.Views;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>
/// The splash exists so that the first thing on screen is never an empty frame: startup runs a schema
/// migration and several filesystem sweeps before the main window has anything to show. What is asserted
/// here is that it says which of those is running, and that it says so on a window that will not be
/// mistaken for the application itself.
/// </summary>
public sealed class SplashTests
{
    [AvaloniaFact]
    public void TheSplashNamesTheStepThatIsRunning()
    {
        var viewModel = new SplashViewModel(AssemblyVersionInfo.Parse("0.8.1+deadbeef"));
        var window = new SplashWindow(viewModel);
        window.Show();
        window.UpdateLayout();

        Assert.Equal("RemoteFlow", viewModel.ProductName);
        Assert.Equal("Version 0.8.1", viewModel.VersionText);
        // Seeded rather than blank: a warm start can close the splash before the first report lands.
        Assert.Equal("Starting", viewModel.StatusMessage);

        // Reported through the interface the startup sequence actually holds, not the concrete class.
        var progress = window.Progress;
        progress.Report("Preparing the database");

        Assert.Equal("Preparing the database", viewModel.StatusMessage);

        // The status has to be a live region, or a screen reader hears the splash open and then nothing
        // while the slowest part of the start goes by.
        var status = window.GetVisualDescendants()
            .OfType<TextBlock>()
            .Single(text => AutomationProperties.GetName(text) == "Startup progress");
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));

        // Indeterminate on purpose: none of the steps behind it can say how far along it is.
        var bar = window.GetVisualDescendants().OfType<ProgressBar>().Single();
        Assert.True(bar.IsIndeterminate);
        window.Close();
    }

    /// <summary>A build with no version recorded shows no version line rather than a confident 0.0.0.
    /// </summary>
    [AvaloniaFact]
    public void AVersionlessBuildShowsNoVersionLine()
    {
        var viewModel = new SplashViewModel();

        Assert.Equal(string.Empty, viewModel.VersionText);
    }

    /// <summary>
    /// A startup failure has to be shown, and startup now fails before there is a main window to own a
    /// dialog. The service used to draw one only when a visible main window existed and otherwise show
    /// nothing at all, which behind a splash would have meant the process disappearing without a word.
    /// </summary>
    [AvaloniaFact]
    public async Task AnErrorIsShownEvenWithNoMainWindowToOwnIt()
    {
        var lifetime = global::Avalonia.Application.Current?.ApplicationLifetime;
        Assert.False(
            lifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.IsVisible: true },
            "This test only means anything while there is no visible main window.");

        // The dialog is ownerless and modeless here, so ShowAsync completes once it has been closed, and
        // closing it is the test's job. Caught through the same class handler the desktop lifetime uses to
        // track windows, because there is no window list to search on a headless lifetime.
        Window? opened = null;
        using var subscription = Window.WindowOpenedEvent.AddClassHandler(
            typeof(Window),
            (sender, _) => opened ??= sender as Window);

        var showing = new ErrorDialogService().ShowAsync("RemoteFlow encountered an error", "Boom");
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(opened);
        Assert.Equal("RemoteFlow encountered an error", opened.Title);
        Assert.True(opened.IsVisible, "A startup failure with no main window drew nothing at all.");
        opened.Close();

        await showing;
    }

    /// <summary>It must not look or behave like the application window: no taskbar entry to click back to,
    /// and nothing to resize or drag, because it is gone in a second or two.</summary>
    [AvaloniaFact]
    public void TheSplashIsNotMistakenForTheApplicationWindow()
    {
        var window = new SplashWindow(new SplashViewModel());

        Assert.False(window.ShowInTaskbar);
        Assert.False(window.CanResize);
        Assert.Equal(WindowDecorations.None, window.WindowDecorations);
        Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
    }
}
