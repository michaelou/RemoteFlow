using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.ViewModels.Settings;
using RemoteFlow.UI.Views.Settings;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>Drives the about box through the control tree rather than the view model, because the thing
/// worth checking is that the buttons are wired to the commands at all. A view model with working
/// commands and a view that binds none of them passes every other test in this project.</summary>
public sealed class AboutViewTests
{
    [AvaloniaFact]
    public void EveryActionInTheAboutBoxReachesItsCommand()
    {
        var (view, shell, _, _) = CreateView();

        Click(view, "OpenLogFolderButton");
        Click(view, "OpenDataFolderButton");
        Click(view, "RepositoryButton");

        Assert.Equal(
            [StubPaths.Logs, StubPaths.Data],
            shell.OpenedFolders);
        Assert.Equal(new Uri(AboutViewModel.RepositoryUrl), Assert.Single(shell.OpenedUrls));
    }

    [AvaloniaFact]
    public void TheVersionCommitLicenceAndPathsAreOnScreen()
    {
        var (view, _, _, _) = CreateView();

        Assert.Equal("0.1.0", Text(view, "VersionText"));
        Assert.Equal("3272ddcdf001f1472bb7358c5e3dd284c19482e3", Text(view, "CommitText"));
        Assert.Equal("MIT", Text(view, "LicenseText"));
        Assert.Equal(StubPaths.Logs, Text(view, "LogDirectoryText"));
        Assert.Equal(StubPaths.Data, Text(view, "DataDirectoryText"));
        Assert.Contains("Third-party notices", Text(view, "NoticesText"), StringComparison.Ordinal);

    }

    // The update section is the one part of the about box that reaches the network, so what it says before
    // anything has happened matters: a button, a description of what pressing it does, and no claim that a
    // check has run.
    [AvaloniaFact]
    public void TheUpdateSectionOffersACheckAndClaimsNothingUntilOneHasRun()
    {
        var (view, _, _, _) = CreateView();

        Assert.True(view.FindControl<Button>("CheckForUpdatesButton")!.IsVisible);
        Assert.False(view.FindControl<SelectableTextBlock>("UpdateStatusText")!.IsVisible);
        Assert.False(view.FindControl<Button>("OpenReleasePageButton")!.IsVisible);
        Assert.False(view.FindControl<CheckBox>("AutomaticUpdateCheckToggle")!.IsChecked);
    }

    [AvaloniaFact]
    public void PressingCheckReachesTheCheckerAndPutsTheResultOnScreen()
    {
        var page = new Uri("https://github.com/michaelou/RemoteFlow/releases/tag/v0.2.0");
        var (view, shell, _, checker) = CreateView(
            updateResult: UpdateCheckResult.UpdateAvailable("0.2.0", page));

        Click(view, "CheckForUpdatesButton");

        Assert.Equal(1, checker.Calls);
        var status = view.FindControl<SelectableTextBlock>("UpdateStatusText")!;
        Assert.True(status.IsVisible);
        Assert.Contains("0.2.0", status.Text!, StringComparison.Ordinal);

        // The link only appears once there is somewhere worth going, and it goes where the check said.
        var releasePage = view.FindControl<Button>("OpenReleasePageButton")!;
        Assert.True(releasePage.IsVisible);
        Click(view, "OpenReleasePageButton");
        Assert.Equal(page, Assert.Single(shell.OpenedUrls));
    }

    [AvaloniaFact]
    public void ACurrentBuildIsToldSoAndIsOfferedNoReleasePage()
    {
        var (view, _, _, _) = CreateView(
            updateResult: UpdateCheckResult.UpToDate("0.1.0", new Uri(AboutViewModel.RepositoryUrl)));

        Click(view, "CheckForUpdatesButton");

        Assert.True(view.FindControl<SelectableTextBlock>("UpdateStatusText")!.IsVisible);
        Assert.False(view.FindControl<Button>("OpenReleasePageButton")!.IsVisible);
    }

    // Ticking the box is the opt-in, and it has to reach the settings store rather than just the screen.
    [AvaloniaFact]
    public void TheAutomaticCheckToggleIsBoundToTheStoredSetting()
    {
        var settings = new InMemorySettingsStore();
        var (view, _, _, _) = CreateView(settings: settings);
        var toggle = view.FindControl<CheckBox>("AutomaticUpdateCheckToggle")!;

        toggle.IsChecked = true;

        Assert.True(settings.Get(SettingKeys.CheckForUpdates, TestContext.Current.CancellationToken)
            .GetAwaiter().GetResult());
    }

    // The normal state of the application is that nothing has crashed, and a panel saying so would be
    // noise on a page people open to read a version number.
    [AvaloniaFact]
    public void TheCrashPanelIsHiddenUntilSomethingFailsAndThenOffersTheLogFolder()
    {
        var (view, shell, store, _) = CreateView();
        var panel = view.FindControl<Border>("LastErrorPanel")!;

        Assert.False(panel.IsVisible);

        store.Raise(new LastError(
            new DateTimeOffset(2026, 8, 9, 14, 30, 0, TimeSpan.Zero),
            "application startup",
            "IOException",
            "the log directory is read-only"));

        Assert.True(panel.IsVisible);
        Assert.Contains("the log directory is read-only", Text(view, "LastErrorText"), StringComparison.Ordinal);

        Click(view, "OpenLogsForErrorButton");
        Assert.Equal(StubPaths.Logs, Assert.Single(shell.OpenedFolders));

        Click(view, "DismissLastErrorButton");
        Assert.False(panel.IsVisible);
    }

    [AvaloniaFact]
    public void AFailureToOpenAFolderIsShownWhereTheButtonIs()
    {
        var (view, _, _, _) = CreateView(ShellOpenResult.Failure("no file manager here"));
        var status = view.FindControl<TextBlock>("StatusText")!;

        Assert.False(status.IsVisible);

        Click(view, "OpenLogFolderButton");

        Assert.True(status.IsVisible);
        Assert.Equal("no file manager here", status.Text);
    }

    private static (AboutView View, RecordingShell Shell, FakeLastErrorStore Store, RecordingUpdateChecker Checker)
        CreateView(
            ShellOpenResult? result = null,
            UpdateCheckResult? updateResult = null,
            ISettingsStore? settings = null)
    {
        var shell = new RecordingShell { Result = result ?? ShellOpenResult.Success };
        var store = new FakeLastErrorStore();
        var checker = new RecordingUpdateChecker
        {
            Result = updateResult ?? UpdateCheckResult.NoReleaseYet(),
        };
        var about = new AboutViewModel(
            AssemblyVersionInfo.Parse("0.1.0+3272ddcdf001f1472bb7358c5e3dd284c19482e3"),
            new StubPaths(),
            shell,
            store,
            updateChecker: checker,
            settings: settings ?? new InMemorySettingsStore());

        // A window, because visibility and bindings only settle once the control is in a visual tree.
        var view = new AboutView { DataContext = about };
        var window = new Window { Content = view, Width = 900, Height = 1200 };
        window.Show();
        return (view, shell, store, checker);
    }

    private static void Click(AboutView view, string name)
    {
        var button = view.FindControl<Button>(name);
        Assert.NotNull(button);
        Assert.NotNull(button.Command);
        // Invoking the command rather than synthesising a pointer press: the assertion is that the button
        // is bound to the right command, and a headless click adds hit-testing to the list of things that
        // can make this test fail for reasons that are not about the about box.
        button.Command.Execute(button.CommandParameter);
    }

    private static string Text(AboutView view, string name)
    {
        var block = view.FindControl<SelectableTextBlock>(name);
        Assert.NotNull(block);
        return block.Text ?? string.Empty;
    }

    private sealed class RecordingUpdateChecker : IUpdateChecker
    {
        public UpdateCheckResult Result { get; init; } = UpdateCheckResult.NoReleaseYet();

        public int Calls { get; private set; }

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class StubPaths : IAppPaths
    {
        public const string Logs = "/tmp/remoteflow-tests/logs";
        public const string Data = "/tmp/remoteflow-tests/data";

        public string ConfigDirectory => Data;

        public string DataDirectory => Data;

        public string CacheDirectory => "/tmp/remoteflow-tests/cache";

        public string LogDirectory => Logs;

        public void EnsureDirectories()
        {
        }
    }

    private sealed class RecordingShell : IShellOpenService
    {
        public ShellOpenResult Result { get; init; } = ShellOpenResult.Success;

        public List<string> OpenedFolders { get; } = [];

        public List<Uri> OpenedUrls { get; } = [];

        public Task<ShellOpenResult> OpenFolderAsync(string path, CancellationToken cancellationToken = default)
        {
            OpenedFolders.Add(path);
            return Task.FromResult(Result);
        }

        public Task<ShellOpenResult> OpenUrlAsync(Uri url, CancellationToken cancellationToken = default)
        {
            OpenedUrls.Add(url);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeLastErrorStore : ILastErrorStore
    {
        public event EventHandler? Changed;

        public LastError? Current { get; private set; }

        public void Raise(LastError error)
        {
            Current = error;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Record(Exception exception, string context, DateTimeOffset occurredAt)
        {
            Raise(new LastError(occurredAt, context, exception.GetType().Name, exception.Message));
        }

        public void Clear()
        {
            Current = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
