using RemoteFlow.Application.Abstractions;
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
