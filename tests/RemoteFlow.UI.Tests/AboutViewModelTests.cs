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

    /// <summary>An available update whose release publishes an installer this machine could verify. Anything
    /// less than that has to leave the install button hidden.</summary>
    private static UpdateCheckResult AvailableWithInstaller()
    {
        return UpdateCheckResult.UpdateAvailable(
            "0.2.0",
            new Uri("https://github.com/michaelou/RemoteFlow/releases/tag/v0.2.0"),
            new UpdatePackage(
                "RemoteFlow-0.2.0-win-x64-setup.exe",
                new Uri("https://github.com/michaelou/RemoteFlow/releases/download/v0.2.0/RemoteFlow-0.2.0-win-x64-setup.exe"),
                90_000_000,
                new Uri("https://github.com/michaelou/RemoteFlow/releases/download/v0.2.0/checksums.txt")));
    }

    [Fact]
    public async Task AnAvailableUpdateWithAVerifiableInstallerOffersToInstallIt()
    {
        var checker = new RecordingUpdateChecker { Result = AvailableWithInstaller() };
        var about = Create(
            checker,
            installer: new RecordingUpdateInstaller(),
            confirmation: new RecordingConfirmation());

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.True(about.CanInstallUpdate);
        Assert.False(about.HasInstallObstacle);
        Assert.Equal("0.2.0", about.LatestVersion);
    }

    /// <summary>The dialog is where the version, the size, and the unsigned-installer caveat are stated, so
    /// a host that cannot show one must not offer the button either.</summary>
    [Fact]
    public async Task WithNowhereToAskTheQuestionThereIsNoInstallButton()
    {
        var checker = new RecordingUpdateChecker { Result = AvailableWithInstaller() };
        var about = Create(checker, installer: new RecordingUpdateInstaller());

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.False(about.CanInstallUpdate);
    }

    /// <summary>Three separate ways the button must stay hidden, and each says why rather than leaving an
    /// absence to puzzle over. A button that would fail after being pressed is worse than no button.</summary>
    [Fact]
    public async Task AReleaseWithNoVerifiableInstallerSaysSoInsteadOfOfferingAButton()
    {
        var checker = new RecordingUpdateChecker
        {
            Result = UpdateCheckResult.UpdateAvailable(
                "0.2.0",
                new Uri("https://github.com/michaelou/RemoteFlow/releases/tag/v0.2.0")),
        };
        var about = Create(
            checker,
            installer: new RecordingUpdateInstaller(),
            confirmation: new RecordingConfirmation());

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.False(about.CanInstallUpdate);
        Assert.True(about.HasInstallObstacle);
        Assert.Contains(
            "release page",
            Assert.IsType<string>(about.InstallObstacle),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task APortableCopyIsToldWhyItCannotInstallTheUpdateItFound()
    {
        var checker = new RecordingUpdateChecker { Result = AvailableWithInstaller() };
        var installer = new RecordingUpdateInstaller
        {
            CanInstall = false,
            Unavailable = "This is a portable copy of RemoteFlow.",
        };
        var about = Create(checker, installer: installer, confirmation: new RecordingConfirmation());

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.False(about.CanInstallUpdate);
        Assert.True(about.HasInstallObstacle);
        Assert.Equal("This is a portable copy of RemoteFlow.", about.InstallObstacle);
    }

    [Fact]
    public async Task ABuildWithNoInstallerAtAllStillChecksAndOffersTheReleasePage()
    {
        var checker = new RecordingUpdateChecker { Result = AvailableWithInstaller() };
        var about = Create(checker);

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.False(about.CanInstallUpdate);
        Assert.False(about.HasInstallObstacle);
        Assert.True(about.IsUpdateAvailable);
    }

    /// <summary>The dialog is the disclosure, so declining it has to mean nothing happened at all — not
    /// a download already taken and discarded.</summary>
    [Fact]
    public async Task DecliningTheConfirmationDownloadsNothingAndSchedulesNothing()
    {
        var checker = new RecordingUpdateChecker { Result = AvailableWithInstaller() };
        var installer = new RecordingUpdateInstaller();
        var confirmation = new RecordingConfirmation { Answer = false };
        var shutdown = new RecordingShutdown();
        var about = Create(checker, installer: installer, confirmation: confirmation, shutdown: shutdown);
        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        await about.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmation.Calls);
        Assert.Equal(0, installer.Downloads);
        Assert.Empty(installer.Scheduled);
        Assert.Equal(0, shutdown.Requests);
    }

    /// <summary>The order is the safety property: nothing is scheduled before it has been verified, and
    /// nothing asks the application to close before something is scheduled.</summary>
    [Fact]
    public async Task AcceptingDownloadsThenSchedulesThenAsksToClose()
    {
        var checker = new RecordingUpdateChecker { Result = AvailableWithInstaller() };
        var order = new List<string>();
        var installer = new RecordingUpdateInstaller { Order = order };
        var shutdown = new RecordingShutdown { Order = order };
        var about = Create(
            checker,
            installer: installer,
            confirmation: new RecordingConfirmation { Answer = true },
            shutdown: shutdown);
        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        await about.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(["download", "schedule", "shutdown"], order);
        Assert.False(about.IsInstallingUpdate);
        Assert.Contains("verified", about.UpdateStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheConfirmationSaysWhatIsCheckedAndWhatIsNot()
    {
        var checker = new RecordingUpdateChecker { Result = AvailableWithInstaller() };
        var confirmation = new RecordingConfirmation { Answer = false };
        var about = Create(
            checker,
            installer: new RecordingUpdateInstaller(),
            confirmation: confirmation);
        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        await about.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Contains("0.2.0", confirmation.Title, StringComparison.Ordinal);
        Assert.Contains("SHA-256", confirmation.Message, StringComparison.Ordinal);
        // The honest half: an integrity check is not an authorship check, and the dialog has to say so.
        Assert.Contains("not code-signed", confirmation.Message, StringComparison.Ordinal);
        Assert.Contains("close", confirmation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedDownloadLeavesTheReasonOnScreenAndSchedulesNothing()
    {
        var checker = new RecordingUpdateChecker { Result = AvailableWithInstaller() };
        var installer = new RecordingUpdateInstaller
        {
            DownloadResult = UpdateDownloadResult.Failed("The download does not match the checksum."),
        };
        var shutdown = new RecordingShutdown();
        var about = Create(
            checker,
            installer: installer,
            confirmation: new RecordingConfirmation { Answer = true },
            shutdown: shutdown);
        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        await about.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal("The download does not match the checksum.", about.UpdateStatus);
        Assert.Empty(installer.Scheduled);
        Assert.Equal(0, shutdown.Requests);
        Assert.False(about.IsInstallingUpdate);
    }

    [Fact]
    public async Task ACancelledDownloadSaysNothingWasInstalled()
    {
        var checker = new RecordingUpdateChecker { Result = AvailableWithInstaller() };
        var installer = new RecordingUpdateInstaller { CancelDuringDownload = true };
        var shutdown = new RecordingShutdown();
        var about = Create(
            checker,
            installer: installer,
            confirmation: new RecordingConfirmation { Answer = true },
            shutdown: shutdown);
        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        await about.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Contains("cancelled", about.UpdateStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(installer.Scheduled);
        Assert.Equal(0, shutdown.Requests);
        Assert.False(about.IsInstallingUpdate);
    }

    [Fact]
    public async Task TheProgressBarFollowsTheDownloadAndTheSentenceDoesNot()
    {
        var checker = new RecordingUpdateChecker { Result = AvailableWithInstaller() };
        var installer = new RecordingUpdateInstaller { ProgressToReport = 0.42 };
        var about = Create(
            checker,
            installer: installer,
            confirmation: new RecordingConfirmation { Answer = true });
        await about.CheckForUpdatesCommand.ExecuteAsync(null);
        // Progress<T> marshals through the synchronization context, which is how the real download reaches
        // the UI thread from a background read. With no context under test it lands on the thread pool
        // instead, so the property change is the thing to wait on rather than the command completing.
        var reported = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        about.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AboutViewModel.UpdateDownloadPercent) &&
                about.UpdateDownloadPercent > 0)
            {
                _ = reported.TrySetResult(about.UpdateDownloadPercent);
            }
        };

        await about.InstallUpdateCommand.ExecuteAsync(null);

        Assert.Equal(42, await reported.Task.WaitAsync(TestContext.Current.CancellationToken));
        // A screen reader re-reading a sentence on every buffer would be worse than useless, so the
        // percentage must never reach it.
        Assert.DoesNotContain("42", about.UpdateStatus, StringComparison.Ordinal);
    }

    /// <summary>An install that never arrived is invisible from inside the application that was replaced,
    /// so the next launch is the only chance to say so.</summary>
    [Fact]
    public async Task AnUpdateThatNeverArrivedIsReportedOnTheNextStart()
    {
        var installer = new RecordingUpdateInstaller
        {
            FailedUpdateReport = "The update to RemoteFlow 0.2.0 did not finish.",
        };
        var about = Create(new RecordingUpdateChecker(), installer: installer);

        await about.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal("The update to RemoteFlow 0.2.0 did not finish.", about.UpdateStatus);
    }

    private static AboutViewModel Create(
        RecordingUpdateChecker checker,
        ISettingsStore? settings = null,
        IUpdateInstaller? installer = null,
        IConfirmationDialogService? confirmation = null,
        IApplicationShutdown? shutdown = null)
    {
        return new AboutViewModel(
            AssemblyVersionInfo.Parse("0.1.0"),
            new StubPaths(),
            new RecordingShell(),
            updateChecker: checker,
            settings: settings,
            installer: installer,
            confirmation: confirmation,
            shutdown: shutdown);
    }

    private sealed class RecordingUpdateInstaller : IUpdateInstaller
    {
        public bool CanInstall { get; init; } = true;

        public string? Unavailable { get; init; }

        public UpdateDownloadResult DownloadResult { get; init; } = UpdateDownloadResult.Verified(
            new VerifiedUpdate(@"C:\cache\Updates\RemoteFlow-0.2.0-win-x64-setup.exe", "0.2.0", "abc123"));

        public string? FailedUpdateReport { get; init; }

        public double? ProgressToReport { get; init; }

        public bool CancelDuringDownload { get; init; }

        /// <summary>Shared with the shutdown fake, so a test can assert the sequence across both.</summary>
        public List<string>? Order { get; init; }

        public int Downloads { get; private set; }

        public List<VerifiedUpdate> Scheduled { get; } = [];

        public Task<UpdateDownloadResult> DownloadAsync(
            UpdatePackage package,
            string version,
            IProgress<double>? progress,
            CancellationToken cancellationToken = default)
        {
            Downloads++;
            Order?.Add("download");
            if (CancelDuringDownload)
            {
                return Task.FromException<UpdateDownloadResult>(new OperationCanceledException());
            }

            if (ProgressToReport is { } fraction)
            {
                progress?.Report(fraction);
            }

            return Task.FromResult(DownloadResult);
        }

        public void ScheduleInstall(VerifiedUpdate update)
        {
            Order?.Add("schedule");
            Scheduled.Add(update);
        }

        public void RunPendingInstall()
        {
            Order?.Add("run");
        }

        public Task<string?> TakeFailedUpdateReportAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(FailedUpdateReport);
        }

        public Task SweepStaleFilesAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingConfirmation : IConfirmationDialogService
    {
        public bool Answer { get; init; }

        public int Calls { get; private set; }

        public string Title { get; private set; } = string.Empty;

        public string Message { get; private set; } = string.Empty;

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string confirmLabel,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Title = title;
            Message = message;
            return Task.FromResult(Answer);
        }
    }

    private sealed class RecordingShutdown : IApplicationShutdown
    {
        public List<string>? Order { get; init; }

        public int Requests { get; private set; }

        public bool Request()
        {
            Requests++;
            Order?.Add("shutdown");
            return true;
        }
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
