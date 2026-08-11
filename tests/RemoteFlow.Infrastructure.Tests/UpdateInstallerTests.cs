using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Infrastructure.Updates;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

/// <summary>Self-update downloads a file and then runs it, so the two questions worth the most tests are
/// "can anything unverified reach the disk under a runnable name" and "is the command line the one the
/// installer script was written for". Both are asserted here without a network and without starting a
/// process.</summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private const string _fileName = "RemoteFlow-0.2.0-win-x64-setup.exe";

    private static readonly byte[] _installerBytes = Encoding.UTF8.GetBytes("a self-contained publish, honestly");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"remoteflow-update-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test directory that outlives the run is untidy, not a failure.
        }
    }

    [Fact]
    public async Task AnInstallerWhoseHashMatchesTheReleaseIsKept()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var context = Create();

        var result = await context.DownloadAsync(cancellationToken);

        var update = Assert.IsType<VerifiedUpdate>(result.Update);
        Assert.Equal(Digest(_installerBytes), update.Sha256);
        Assert.Equal("0.2.0", update.Version);
        Assert.Equal(_installerBytes, await File.ReadAllBytesAsync(update.InstallerPath, cancellationToken));
    }

    [Fact]
    public async Task ProgressRunsForwardsAndEndsAtOne()
    {
        using var context = Create();
        // Not Progress<T>: that posts to the synchronization context, so the reports would still be in
        // flight when the assertions run.
        var reports = new RecordingProgress();

        _ = await context.DownloadAsync(TestContext.Current.CancellationToken, reports);

        Assert.NotEmpty(reports.Fractions);
        Assert.All(reports.Fractions, fraction => Assert.InRange(fraction, 0, 1));
        Assert.Equal(reports.Fractions, [.. reports.Fractions.Order()]);
        Assert.Equal(1d, reports.Fractions[^1]);
    }

    /// <summary>The case the whole design exists for. One byte different means this is not the file the
    /// release published, and what must not survive is anything under a name that would run.</summary>
    [Fact]
    public async Task AnInstallerThatDoesNotMatchItsChecksumLeavesNothingBehind()
    {
        var tampered = (byte[])_installerBytes.Clone();
        tampered[0] ^= 0xFF;
        using var context = Create(installerBody: tampered);

        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains(
            "does not match the checksum",
            Assert.IsType<string>(result.ErrorMessage),
            StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(context.DownloadDirectory));
    }

    /// <summary>The digest is fetched before the installer, so a release that could never be verified costs
    /// one small request rather than ninety megabytes. Asserting on what was requested is the only way to
    /// pin the ordering.</summary>
    [Fact]
    public async Task AMissingChecksumsFileStopsTheDownloadBeforeItStarts()
    {
        using var context = Create(checksumsStatus: HttpStatusCode.NotFound);

        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            context.Handler.Requested,
            url => url.AbsolutePath.EndsWith(_fileName, StringComparison.Ordinal));
        Assert.False(Directory.Exists(context.DownloadDirectory));
    }

    [Fact]
    public async Task AChecksumsFileThatDoesNotListThisInstallerStopsTheDownload()
    {
        using var context = Create(
            checksums: "0000000000000000000000000000000000000000000000000000000000000000  something-else.exe\n");

        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("does not list", Assert.IsType<string>(result.ErrorMessage), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChecksumsFileTooLargeToBeOneIsRefused()
    {
        using var context = Create(checksums: new string('a', Sha256Checksums.MaximumSizeInBytes + 10));

        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task TheRedirectGitHubActuallyIssuesIsFollowed()
    {
        using var context = Create(
            redirectTo: new Uri("https://objects.githubusercontent.com/github-production-release-asset/1?token=abc"));

        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
    }

    /// <summary>A hop is checked before it is requested, so a response pointing elsewhere results in no
    /// connection to that host at all — which is also what keeps GitHub's signed token from being sent
    /// somewhere it was not issued for.</summary>
    [Fact]
    public async Task ARedirectAwayFromGitHubIsNeverContacted()
    {
        using var context = Create(redirectTo: new Uri("https://elsewhere.example/installer.exe"));

        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.DoesNotContain(
            context.Handler.Requested,
            url => url.Host.Equals("elsewhere.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARedirectToPlainHttpIsRefusedTheSameWay()
    {
        using var context = Create(
            redirectTo: new Uri("http://objects.githubusercontent.com/github-production-release-asset/1"));

        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task ARedirectLoopIsAbandonedRatherThanFollowedForever()
    {
        using var context = Create(redirectLoop: true);

        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CancellingMidDownloadLeavesNothingBehind()
    {
        using var context = Create(blockUntilCancelled: true);
        using var cancellation = new CancellationTokenSource();

        var download = context.DownloadAsync(cancellation.Token);
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        Assert.Empty(Directory.GetFiles(context.DownloadDirectory));
    }

    /// <summary>The regression test for the update itself. Every switch here is load-bearing and several
    /// are load-bearing by their absence, so the whole list is asserted rather than a sample of it: /DIR is
    /// missing so Setup reads its own uninstall entry, /TASKS is missing so the user's desktop-icon choice
    /// survives, and /SUPPRESSMSGBOXES is missing because nothing will be running to see a suppressed
    /// message.</summary>
    [Fact]
    public async Task TheInstallerIsStartedWithExactlyTheSwitchesTheSetupScriptExpects()
    {
        using var context = Create();
        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);
        var update = Assert.IsType<VerifiedUpdate>(result.Update);

        context.Installer.ScheduleInstall(update);
        context.Installer.RunPendingInstall();

        var launch = Assert.Single(context.Processes.Requests);
        Assert.Equal(update.InstallerPath, launch.FileName);
        string[] expected =
        [
            "/SILENT",
            "/NOCANCEL",
            "/NORESTART",
            "/UPDATE",
            $"/LOG={Path.Combine(context.LogDirectory, "update-0.2.0.log")}",
        ];
        Assert.Equal(expected, launch.Arguments);
        Assert.False(launch.UseShellExecute);
        Assert.Equal(Path.GetTempPath(), launch.WorkingDirectory);
    }

    /// <summary>Setup checks AppMutex before it touches a file. If this process still holds the mutex by
    /// then, Setup stops and asks a user who is watching the window disappear.</summary>
    [Fact]
    public async Task TheRunningInstanceMutexIsDroppedBeforeTheInstallerStarts()
    {
        var order = new List<string>();
        using var context = Create(onReleaseMutex: () => order.Add("mutex"));
        context.Processes.OnRun = () => order.Add("launch");
        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);

        context.Installer.ScheduleInstall(Assert.IsType<VerifiedUpdate>(result.Update));
        context.Installer.RunPendingInstall();

        Assert.Equal(["mutex", "launch"], order);
    }

    [Fact]
    public void NothingScheduledStartsNothing()
    {
        using var context = Create();

        context.Installer.RunPendingInstall();

        Assert.Empty(context.Processes.Requests);
    }

    [Fact]
    public async Task RunningTheSameQueuedInstallTwiceStartsItOnce()
    {
        using var context = Create();
        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);
        context.Installer.ScheduleInstall(Assert.IsType<VerifiedUpdate>(result.Update));

        context.Installer.RunPendingInstall();
        context.Installer.RunPendingInstall();

        _ = Assert.Single(context.Processes.Requests);
    }

    [Fact]
    public async Task APortableCopyNeitherDownloadsNorInstalls()
    {
        using var context = Create(shape: InstallShape.Portable);

        var result = await context.DownloadAsync(TestContext.Current.CancellationToken);
        context.Installer.ScheduleInstall(new VerifiedUpdate(@"C:\anywhere\setup.exe", "0.2.0", "abc"));
        context.Installer.RunPendingInstall();

        Assert.False(context.Installer.CanInstall);
        Assert.False(result.Succeeded);
        Assert.NotNull(context.Installer.Unavailable);
        Assert.Empty(context.Processes.Requests);
    }

    /// <summary>Inno rolls back by removing what it installed rather than restoring what it replaced, so a
    /// failed update can leave no application at all. Nothing is running to notice, which makes the marker
    /// written before the exit the only way the next launch can say what happened.</summary>
    [Fact]
    public async Task AnUpdateThatNeverArrivedIsReportedAtTheNextLaunch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var context = Create();
        var result = await context.DownloadAsync(cancellationToken);
        context.Installer.ScheduleInstall(Assert.IsType<VerifiedUpdate>(result.Update));
        context.Installer.RunPendingInstall();

        var report = await context.Installer.TakeFailedUpdateReportAsync(cancellationToken);

        // The running build is not 0.2.0 — under test it is whatever MinVer stamped — so the marker written
        // by RunPendingInstall reads as an update that did not take.
        Assert.Contains("0.2.0", Assert.IsType<string>(report), StringComparison.Ordinal);
        Assert.Contains("update-0.2.0.log", Assert.IsType<string>(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReportIsGivenOnceAndThenForgotten()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var context = Create();
        var result = await context.DownloadAsync(cancellationToken);
        context.Installer.ScheduleInstall(Assert.IsType<VerifiedUpdate>(result.Update));
        context.Installer.RunPendingInstall();

        _ = await context.Installer.TakeFailedUpdateReportAsync(cancellationToken);
        var second = await context.Installer.TakeFailedUpdateReportAsync(cancellationToken);

        Assert.Null(second);
    }

    [Fact]
    public async Task WithNoUpdateEverStartedThereIsNothingToReport()
    {
        using var context = Create();

        Assert.Null(await context.Installer.TakeFailedUpdateReportAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The sweep runs at startup rather than after an install, because until RemoteFlow has started
    /// again the downloaded installer is the only way back from an install that destroyed what it
    /// replaced.</summary>
    [Fact]
    public async Task TheSweepClearsOldInstallersButKeepsTheMarkerForItsReader()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var context = Create();
        var result = await context.DownloadAsync(cancellationToken);
        var update = Assert.IsType<VerifiedUpdate>(result.Update);
        context.Installer.ScheduleInstall(update);
        context.Installer.RunPendingInstall();

        await context.Installer.SweepStaleFilesAsync(cancellationToken);

        Assert.False(File.Exists(update.InstallerPath));
        Assert.NotNull(await context.Installer.TakeFailedUpdateReportAsync(cancellationToken));
    }

    [Fact]
    public async Task TheSweepIsHappyWhenNothingHasEverBeenDownloaded()
    {
        using var context = Create();

        await context.Installer.SweepStaleFilesAsync(TestContext.Current.CancellationToken);
    }

    private static string Digest(byte[] content)
    {
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    private Context Create(
        byte[]? installerBody = null,
        string? checksums = null,
        HttpStatusCode checksumsStatus = HttpStatusCode.OK,
        Uri? redirectTo = null,
        bool redirectLoop = false,
        bool blockUntilCancelled = false,
        InstallShape shape = InstallShape.Installer,
        Action? onReleaseMutex = null)
    {
        var handler = new RoutingHandler
        {
            InstallerBody = installerBody ?? _installerBytes,
            Checksums = checksums ?? $"{Digest(_installerBytes)}  {_fileName}\n",
            ChecksumsStatus = checksumsStatus,
            RedirectTo = redirectTo,
            RedirectLoop = redirectLoop,
            BlockUntilCancelled = blockUntilCancelled,
        };
        var paths = new TempPaths(_root);
        var downloader = new ReleaseAssetDownloader(
            new StubVersionInfo(),
            paths,
            NullLogger<ReleaseAssetDownloader>.Instance,
            handler);
        var processes = new RecordingProcessRunner();
        var installer = new UpdateInstaller(
            new StubInstallInfo(shape),
            paths,
            processes,
            downloader,
            NullLogger<UpdateInstaller>.Instance,
            onReleaseMutex ?? (() => { }));

        return new Context(handler, downloader, installer, processes, paths);
    }

    private sealed record Context(
        RoutingHandler Handler,
        ReleaseAssetDownloader Downloader,
        UpdateInstaller Installer,
        RecordingProcessRunner Processes,
        TempPaths Paths) : IDisposable
    {
        public string DownloadDirectory => Downloader.DownloadDirectory;

        public string LogDirectory => Paths.LogDirectory;

        public Task<UpdateDownloadResult> DownloadAsync(
            CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            var package = new UpdatePackage(
                _fileName,
                new Uri($"https://github.com/michaelou/RemoteFlow/releases/download/v0.2.0/{_fileName}"),
                _installerBytes.Length,
                new Uri("https://github.com/michaelou/RemoteFlow/releases/download/v0.2.0/checksums.txt"));
            return Installer.DownloadAsync(package, "0.2.0", progress, cancellationToken);
        }

        public void Dispose()
        {
            Downloader.Dispose();
            Handler.Dispose();
        }
    }

    private sealed class TempPaths(string root) : IAppPaths
    {
        public string ConfigDirectory { get; } = Path.Combine(root, "config");

        public string DataDirectory { get; } = Path.Combine(root, "data");

        public string CacheDirectory { get; } = Path.Combine(root, "cache");

        public string LogDirectory { get; } = Path.Combine(root, "logs");

        public void EnsureDirectories()
        {
            _ = Directory.CreateDirectory(ConfigDirectory);
            _ = Directory.CreateDirectory(DataDirectory);
            _ = Directory.CreateDirectory(CacheDirectory);
            _ = Directory.CreateDirectory(LogDirectory);
        }
    }

    private sealed class StubVersionInfo : IAppVersionInfo
    {
        public string Version => "0.1.0";

        public string? CommitSha => null;
    }

    private sealed class StubInstallInfo(InstallShape shape) : IAppInstallInfo
    {
        public InstallShape Shape { get; } = shape;

        public string InstallDirectory => @"C:\Users\someone\AppData\Local\Programs\RemoteFlow";

        public string? Explanation { get; } = shape == InstallShape.Installer
            ? null
            : "This is a portable copy of RemoteFlow.";
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Fractions { get; } = [];

        public void Report(double value)
        {
            Fractions.Add(value);
        }
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessLaunchRequest> Requests { get; } = [];

        public Action? OnRun { get; set; }

        public Task RunAsync(ProcessLaunchRequest request, CancellationToken cancellationToken = default)
        {
            OnRun?.Invoke();
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers by URL rather than with one canned response, so a test can assert both what came
    /// back and — for the redirect cases — what was never asked for.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        public List<Uri> Requested { get; } = [];

        public byte[] InstallerBody { get; init; } = [];

        public string Checksums { get; init; } = string.Empty;

        public HttpStatusCode ChecksumsStatus { get; init; } = HttpStatusCode.OK;

        public Uri? RedirectTo { get; init; }

        public bool RedirectLoop { get; init; }

        public bool BlockUntilCancelled { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Requested.Add(url);

            if (url.AbsolutePath.EndsWith("checksums.txt", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(ChecksumsStatus)
                {
                    Content = new StringContent(Checksums, Encoding.UTF8, "text/plain"),
                };
            }

            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (RedirectLoop)
            {
                var looping = new HttpResponseMessage(HttpStatusCode.Found);
                looping.Headers.Location = url;
                return looping;
            }

            if (RedirectTo is not null && !url.Host.EndsWith("githubusercontent.com", StringComparison.Ordinal))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = RedirectTo;
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(InstallerBody),
            };
        }
    }
}
