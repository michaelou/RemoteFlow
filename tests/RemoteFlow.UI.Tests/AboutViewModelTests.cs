using RemoteFlow.Application.Abstractions;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.Settings;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class AboutViewModelTests
{
    [Fact]
    public void TheAboutBoxShowsTheVersionAndTheFullCommit()
    {
        var about = new AboutViewModel(AssemblyVersionInfo.Parse(
            "0.1.0+3272ddcdf001f1472bb7358c5e3dd284c19482e3"));

        Assert.Equal("RemoteFlow", about.ProductName);
        Assert.Equal("0.1.0", about.Version);
        Assert.Equal("3272ddcdf001f1472bb7358c5e3dd284c19482e3", about.Commit);
    }

    [Fact]
    public void ABuildWithoutACommitSaysUnknownRatherThanShowingNothing()
    {
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"));

        Assert.Equal("unknown", about.Commit);
    }

    [Fact]
    public void TheSettingsPageCarriesTheAboutBox()
    {
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0+abc1234"));

        var settings = new SettingsPageViewModel(about: about);

        Assert.Same(about, settings.About);
    }

    [Fact]
    public void TheLicenceAndTheRepositoryAreStated()
    {
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"));

        Assert.Equal("MIT", about.License);
        Assert.Equal("https://github.com/michaelou/RemoteFlow", about.Repository);
    }

    // The paths are shown, not only opened: a path can be read out over a support conversation, and it is
    // the answer when the file manager itself will not start.
    [Fact]
    public void TheFoldersAreNamedAsWellAsOpenable()
    {
        var paths = new StubPaths();

        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"), paths);

        Assert.Equal(paths.LogDirectory, about.LogDirectory);
        Assert.Equal(paths.DataDirectory, about.DataDirectory);
    }

    [Fact]
    public async Task OpeningTheLogFolderAsksTheShellForTheLogDirectory()
    {
        var paths = new StubPaths();
        var shell = new RecordingShell();
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"), paths, shell);

        await about.OpenLogFolderCommand.ExecuteAsync(null);

        Assert.Equal(paths.LogDirectory, Assert.Single(shell.OpenedFolders));
        Assert.Equal(string.Empty, about.StatusText);
    }

    [Fact]
    public async Task OpeningTheDataFolderAsksTheShellForTheDataDirectory()
    {
        var paths = new StubPaths();
        var shell = new RecordingShell();
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"), paths, shell);

        await about.OpenDataFolderCommand.ExecuteAsync(null);

        Assert.Equal(paths.DataDirectory, Assert.Single(shell.OpenedFolders));
    }

    [Fact]
    public async Task OpeningTheRepositoryOpensTheProjectPageAndNothingElse()
    {
        var shell = new RecordingShell();
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"), new StubPaths(), shell);

        await about.OpenRepositoryCommand.ExecuteAsync(null);

        Assert.Equal(new Uri("https://github.com/michaelou/RemoteFlow"), Assert.Single(shell.OpenedUrls));
    }

    // None of these actions is worth interrupting someone with a dialog over.
    [Fact]
    public async Task AFileManagerThatWillNotStartLeavesASentenceOnScreen()
    {
        var shell = new RecordingShell { Result = ShellOpenResult.Failure("no file manager here") };
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"), new StubPaths(), shell);

        await about.OpenLogFolderCommand.ExecuteAsync(null);

        Assert.Equal("no file manager here", about.StatusText);
    }

    [Fact]
    public void TheCrashSectionIsHiddenUntilSomethingFails()
    {
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"), new StubPaths(), new RecordingShell());

        Assert.False(about.HasLastError);
        Assert.Equal(string.Empty, about.LastErrorSummary);
    }

    [Fact]
    public void AnErrorRecordedWhileTheAboutBoxIsOpenAppearsInIt()
    {
        var store = new FakeLastErrorStore();
        var about = new AboutViewModel(
            AssemblyVersionInfo.Parse("0.1.0"),
            new StubPaths(),
            new RecordingShell(),
            store);

        store.Raise(new LastError(
            new DateTimeOffset(2026, 8, 9, 14, 30, 0, TimeSpan.Zero),
            "application startup",
            "IOException",
            "the log directory is read-only"));

        Assert.True(about.HasLastError);
        Assert.Contains("IOException", about.LastErrorSummary, StringComparison.Ordinal);
        Assert.Contains("the log directory is read-only", about.LastErrorSummary, StringComparison.Ordinal);
        Assert.Contains("application startup", about.LastErrorSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void DismissingTheErrorClearsItEverywhereRatherThanJustOnScreen()
    {
        var store = new FakeLastErrorStore();
        var about = new AboutViewModel(
            AssemblyVersionInfo.Parse("0.1.0"),
            new StubPaths(),
            new RecordingShell(),
            store);
        store.Raise(new LastError(DateTimeOffset.UnixEpoch, "a background task", "IOException", "boom"));

        about.DismissLastErrorCommand.Execute(null);

        Assert.False(about.HasLastError);
        Assert.Null(store.Current);
    }

    [Fact]
    public void DisposingStopsListeningSoTheSingletonDoesNotOutliveTheSubscription()
    {
        var store = new FakeLastErrorStore();
        var about = new AboutViewModel(
            AssemblyVersionInfo.Parse("0.1.0"),
            new StubPaths(),
            new RecordingShell(),
            store);

        about.Dispose();
        store.Raise(new LastError(DateTimeOffset.UnixEpoch, "a background task", "IOException", "boom"));

        Assert.False(about.HasLastError);
    }

    // The whole feature is opt-in twice over: nothing happens until the button is pressed, or until the
    // setting that is off by default has been switched on.
    [Fact]
    public async Task NothingIsCheckedUntilSomethingAsksForIt()
    {
        var checker = new RecordingUpdateChecker();
        var about = Create(checker, new InMemorySettingsStore());

        await about.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, checker.Calls);
        Assert.False(about.AutomaticUpdateCheckEnabled);
        Assert.Equal(string.Empty, about.UpdateStatus);
        Assert.False(about.HasUpdateStatus);
    }

    [Fact]
    public async Task PressingTheButtonChecksAndNamesTheNewerRelease()
    {
        var page = new Uri("https://github.com/michaelou/RemoteFlow/releases/tag/v0.2.0");
        var checker = new RecordingUpdateChecker
        {
            Result = UpdateCheckResult.UpdateAvailable("0.2.0", page),
        };
        var about = Create(checker);

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, checker.Calls);
        Assert.Contains("0.2.0", about.UpdateStatus, StringComparison.Ordinal);
        Assert.True(about.IsUpdateAvailable);
        Assert.Equal(page, about.ReleasePageUrl);
        Assert.False(about.IsCheckingForUpdates);
    }

    // Sending someone who is already current to a download page invites them to reinstall what they run.
    [Fact]
    public async Task ACurrentBuildIsToldSoAndOfferedNoDownloadLink()
    {
        var checker = new RecordingUpdateChecker
        {
            Result = UpdateCheckResult.UpToDate("0.1.0", new Uri("https://github.com/michaelou/RemoteFlow")),
        };
        var about = Create(checker);

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Contains("current", about.UpdateStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(about.IsUpdateAvailable);
        Assert.Null(about.ReleasePageUrl);
    }

    [Fact]
    public async Task AnUnpublishedProjectIsDistinguishedFromAFailedCheck()
    {
        var checker = new RecordingUpdateChecker { Result = UpdateCheckResult.NoReleaseYet() };
        var about = Create(checker);

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Contains("no published releases", about.UpdateStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(about.IsUpdateAvailable);
    }

    [Fact]
    public async Task AFailedCheckLeavesTheReasonOnScreenRatherThanThrowing()
    {
        var checker = new RecordingUpdateChecker
        {
            Result = UpdateCheckResult.Failed("No such host is known."),
        };
        var about = Create(checker);

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("No such host is known.", about.UpdateStatus);
        Assert.False(about.IsUpdateAvailable);
        Assert.False(about.IsCheckingForUpdates);
    }

    // One of the callers does not await this, so a checker that throws must not take the process with it.
    [Fact]
    public async Task ACheckerThatThrowsIsReportedRatherThanEscaping()
    {
        var checker = new RecordingUpdateChecker { Failure = new InvalidOperationException("boom") };
        var about = Create(checker);

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Contains("boom", about.UpdateStatus, StringComparison.Ordinal);
        Assert.False(about.IsCheckingForUpdates);
    }

    [Fact]
    public async Task TheReleasePageButtonOpensTheLinkTheCheckReturnedAndNothingElse()
    {
        var page = new Uri("https://github.com/michaelou/RemoteFlow/releases/tag/v0.2.0");
        var shell = new RecordingShell();
        var checker = new RecordingUpdateChecker
        {
            Result = UpdateCheckResult.UpdateAvailable("0.2.0", page),
        };
        var about = new AboutViewModel(
            AssemblyVersionInfo.Parse("0.1.0"),
            new StubPaths(),
            shell,
            updateChecker: checker);
        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        await about.OpenReleasePageCommand.ExecuteAsync(null);

        Assert.Equal(page, Assert.Single(shell.OpenedUrls));
    }

    [Fact]
    public async Task WithNothingToOpenTheReleasePageCommandDoesNothing()
    {
        var shell = new RecordingShell();
        var about = new AboutViewModel(
            AssemblyVersionInfo.Parse("0.1.0"),
            new StubPaths(),
            shell,
            updateChecker: new RecordingUpdateChecker());

        await about.OpenReleasePageCommand.ExecuteAsync(null);

        Assert.Empty(shell.OpenedUrls);
    }

    [Fact]
    public async Task SwitchingTheOptInOnStoresItAndChecksStraightAwayRatherThanNextLaunch()
    {
        var settings = new InMemorySettingsStore();
        var checker = new RecordingUpdateChecker();
        var about = Create(checker, settings);
        await about.InitializeAsync(TestContext.Current.CancellationToken);

        about.AutomaticUpdateCheckEnabled = true;

        Assert.True(await settings.Get(SettingKeys.CheckForUpdates, TestContext.Current.CancellationToken));
        Assert.Equal(1, checker.Calls);
    }

    [Fact]
    public async Task SwitchingTheOptInOffStoresThatToo()
    {
        var settings = new InMemorySettingsStore();
        await settings.Set(SettingKeys.CheckForUpdates, true, TestContext.Current.CancellationToken);
        var about = Create(new RecordingUpdateChecker(), settings);
        await about.InitializeAsync(TestContext.Current.CancellationToken);

        about.AutomaticUpdateCheckEnabled = false;

        Assert.False(await settings.Get(SettingKeys.CheckForUpdates, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheStoredOptInIsHonouredOnStartupAndTheCheckRunsOnce()
    {
        var settings = new InMemorySettingsStore();
        await settings.Set(SettingKeys.CheckForUpdates, true, TestContext.Current.CancellationToken);
        var checker = new RecordingUpdateChecker();
        var about = Create(checker, settings);

        await about.InitializeAsync(TestContext.Current.CancellationToken);
        // The startup path and the page's own Loaded handler both call this; it must not check twice.
        await about.InitializeAsync(TestContext.Current.CancellationToken);
        await checker.Completed;

        Assert.True(about.AutomaticUpdateCheckEnabled);
        Assert.Equal(1, checker.Calls);
    }

    // A host that registered no checker gets an about box without the section, rather than a dead button.
    [Fact]
    public void AbuildWithNoUpdateCheckerHidesTheSectionRatherThanShowingADeadButton()
    {
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"));

        Assert.False(about.CanCheckForUpdates);
    }

    [Fact]
    public void AbuildWithAnUpdateCheckerShowsTheSection()
    {
        Assert.True(Create(new RecordingUpdateChecker()).CanCheckForUpdates);
    }

    // Attribution has to travel with the binary: a user with an extracted portable zip has nothing else.
    [Fact]
    public void TheThirdPartyNoticesAreEmbeddedInTheAssembly()
    {
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"));

        Assert.Contains("Third-party notices", about.Notices, StringComparison.Ordinal);
        Assert.Contains("Avalonia", about.Notices, StringComparison.Ordinal);
        Assert.Contains("MIT", about.Notices, StringComparison.Ordinal);
        Assert.Same(ThirdPartyNotices.Text, about.Notices);
    }

    private static AboutViewModel Create(
        RecordingUpdateChecker checker,
        ISettingsStore? settings = null)
    {
        return new AboutViewModel(
            AssemblyVersionInfo.Parse("0.1.0"),
            new StubPaths(),
            new RecordingShell(),
            updateChecker: checker,
            settings: settings);
    }

    private sealed class RecordingUpdateChecker : IUpdateChecker
    {
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UpdateCheckResult Result { get; init; } = UpdateCheckResult.NoReleaseYet();

        public Exception? Failure { get; init; }

        public int Calls { get; private set; }

        /// <summary>Completes when the first check has run, so a test can wait for the one the view model
        /// deliberately starts without awaiting.</summary>
        public Task Completed => _completed.Task;

        public Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            _ = _completed.TrySetResult();
            return Failure is null
                ? Task.FromResult(Result)
                : Task.FromException<UpdateCheckResult>(Failure);
        }
    }

    private sealed class StubPaths : IAppPaths
    {
        public string ConfigDirectory { get; } = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", "config");

        public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", "data");

        public string CacheDirectory { get; } = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", "cache");

        public string LogDirectory { get; } = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", "logs");

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
