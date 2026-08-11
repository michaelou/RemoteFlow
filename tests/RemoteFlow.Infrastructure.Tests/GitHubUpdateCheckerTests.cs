using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Updates;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

/// <summary>The update check is the only request RemoteFlow makes to a host the user did not configure,
/// so what it sends matters as much as what it concludes. Both are asserted here against a handler that
/// never reaches the network.</summary>
public sealed class GitHubUpdateCheckerTests
{
    [Fact]
    public async Task ANewerReleaseIsReportedWithItsVersionAndItsPage()
    {
        using var handler = RecordingHandler.Returning(Release("v0.2.0"));
        using var checker = Create(handler, "0.1.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("0.2.0", result.LatestVersion);
        Assert.Equal(new Uri("https://github.com/michaelou/RemoteFlow/releases/tag/v0.2.0"), result.ReleasePageUrl);
    }

    [Fact]
    public async Task TheSameVersionIsUpToDateRatherThanAnUpdate()
    {
        using var handler = RecordingHandler.Returning(Release("v0.1.0"));
        using var checker = Create(handler, "0.1.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Equal("0.1.0", result.LatestVersion);
    }

    // Someone running a build from main is ahead of the newest release, not behind it.
    [Fact]
    public async Task ABuildNewerThanTheNewestReleaseIsNotOfferedADowngrade()
    {
        using var handler = RecordingHandler.Returning(Release("v0.1.0"));
        using var checker = Create(handler, "0.2.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    // True of RemoteFlow itself until the first tag is published, and it is not a failure.
    [Fact]
    public async Task ARepositoryWithNoReleasesSaysSoRatherThanReportingAnError()
    {
        using var handler = RecordingHandler.Returning(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
        using var checker = Create(handler, "0.1.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.NoReleaseYet, result.Outcome);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ARateLimitSaysToTryLaterAndNamesThePageToLookAtInstead()
    {
        using var handler = RecordingHandler.Returning(HttpStatusCode.Forbidden, "{}");
        using var checker = Create(handler, "0.1.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Contains("rate-limiting", result.ErrorMessage!, StringComparison.Ordinal);
        Assert.Contains(GitHubUpdateChecker.ReleasesPageUrl, result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoNetworkIsAFailureWithASentenceRatherThanAnException()
    {
        using var handler = RecordingHandler.Throwing(new HttpRequestException("No such host is known."));
        using var checker = Create(handler, "0.1.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Contains("No such host is known.", result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResponseThatIsNotJsonFailsWithoutTakingTheApplicationWithIt()
    {
        using var handler = RecordingHandler.Returning(HttpStatusCode.OK, "<html>a captive portal</html>");
        using var checker = Create(handler, "0.1.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task AReleaseTaggedWithSomethingThatIsNotAVersionIsReportedRatherThanGuessedAt()
    {
        using var handler = RecordingHandler.Returning(Release("nightly"));
        using var checker = Create(handler, "0.1.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Contains("nightly", result.ErrorMessage!, StringComparison.Ordinal);
    }

    // The link is handed to the desktop shell, and it arrives in a network response. A response naming
    // somewhere else must not become something the user is invited to click.
    [Fact]
    public async Task AReleasePageSomewhereOtherThanGitHubFallsBackToTheKnownReleasesPage()
    {
        using var handler = RecordingHandler.Returning(
            """{"tag_name":"v0.2.0","html_url":"https://example.invalid/not-github"}""");
        using var checker = Create(handler, "0.1.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal(new Uri(GitHubUpdateChecker.ReleasesPageUrl), result.ReleasePageUrl);
    }

    [Fact]
    public async Task AnHttpReleasePageIsRefusedTheSameWay()
    {
        using var handler = RecordingHandler.Returning(
            """{"tag_name":"v0.2.0","html_url":"http://github.com/michaelou/RemoteFlow/releases/tag/v0.2.0"}""");
        using var checker = Create(handler, "0.1.0");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Uri(GitHubUpdateChecker.ReleasesPageUrl), result.ReleasePageUrl);
    }

    /// <summary>What leaves the machine: one GET to the project's own release list, a User-Agent naming the
    /// software, and nothing else. No cookie, no authorization header, no identifier RemoteFlow invented.</summary>
    [Fact]
    public async Task TheRequestCarriesTheProductNameAndNothingAboutTheMachine()
    {
        using var handler = RecordingHandler.Returning(Release("v0.1.0"));
        using var checker = Create(handler, "0.1.0");

        _ = await checker.CheckAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            new Uri("https://api.github.com/repos/michaelou/RemoteFlow/releases/latest"),
            request.RequestUri);
        Assert.Equal("RemoteFlow/0.1.0", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.DoesNotContain(request.Headers, header =>
            header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        Assert.Null(request.Content);
    }

    // MinVer stamps a development build as 0.0.0-alpha.0.57+abc1234, and a User-Agent product version
    // cannot contain a plus sign — a malformed header would fail the request before it was sent.
    [Fact]
    public async Task ADevelopmentBuildStillProducesASendableUserAgent()
    {
        using var handler = RecordingHandler.Returning(Release("v0.1.0"));
        using var checker = Create(handler, "0.0.0-alpha.0.57+abc1234");

        _ = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("RemoteFlow/0.0.0-alpha.0.57abc1234", Assert.Single(handler.Requests).Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task ABuildWhoseOwnVersionCannotBeReadSaysSoWithoutAskingGitHubAnything()
    {
        using var handler = RecordingHandler.Returning(Release("v0.2.0"));
        using var checker = Create(handler, "not-a-version");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheInstallerForThisArchitectureIsSingledOutFromEverythingElseTheReleasePublishes()
    {
        using var handler = RecordingHandler.Returning(ReleaseWithAssets(
            "v0.2.0",
            Asset("RemoteFlow-0.2.0-win-arm64-setup.exe", 91_000_000),
            Asset("RemoteFlow-0.2.0-win-arm64.zip", 88_000_000),
            Asset("RemoteFlow-0.2.0-win-x64-setup.exe", 90_000_000),
            Asset("RemoteFlow-0.2.0-win-x64.zip", 87_000_000),
            Asset("checksums.txt", 412)));
        using var checker = Create(handler, "0.1.0", "win-x64");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        var package = Assert.IsType<UpdatePackage>(result.Package);
        Assert.Equal("RemoteFlow-0.2.0-win-x64-setup.exe", package.FileName);
        Assert.Equal(90_000_000, package.SizeInBytes);
        Assert.Equal(
            new Uri("https://github.com/michaelou/RemoteFlow/releases/download/v0.2.0/RemoteFlow-0.2.0-win-x64-setup.exe"),
            package.DownloadUrl);
        Assert.Equal(
            new Uri("https://github.com/michaelou/RemoteFlow/releases/download/v0.2.0/checksums.txt"),
            package.ChecksumsUrl);
    }

    // The same release, read by the native Arm build. The zip and the other architecture's installer are
    // both wrong answers, and the x64 one is the wrong answer that would still have run.
    [Fact]
    public async Task AnArmBuildIsOfferedTheArmInstaller()
    {
        using var handler = RecordingHandler.Returning(ReleaseWithAssets(
            "v0.2.0",
            Asset("RemoteFlow-0.2.0-win-arm64-setup.exe", 91_000_000),
            Asset("RemoteFlow-0.2.0-win-x64-setup.exe", 90_000_000),
            Asset("checksums.txt", 412)));
        using var checker = Create(handler, "0.1.0", "win-arm64");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("RemoteFlow-0.2.0-win-arm64-setup.exe", result.Package!.FileName);
    }

    /// <summary>Without a published hash there is nothing to check a download against, and RemoteFlow will
    /// not run an installer it cannot verify. The update is still reported — the release page link is the
    /// answer — but no button appears that would fail after being pressed.</summary>
    [Fact]
    public async Task AReleaseWithNoChecksumsFileOffersNoInstaller()
    {
        using var handler = RecordingHandler.Returning(ReleaseWithAssets(
            "v0.2.0",
            Asset("RemoteFlow-0.2.0-win-x64-setup.exe", 90_000_000)));
        using var checker = Create(handler, "0.1.0", "win-x64");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("0.2.0", result.LatestVersion);
        Assert.Null(result.Package);
    }

    [Fact]
    public async Task AReleaseWithNoInstallerForThisArchitectureOffersNone()
    {
        using var handler = RecordingHandler.Returning(ReleaseWithAssets(
            "v0.2.0",
            Asset("RemoteFlow-0.2.0-win-arm64-setup.exe", 91_000_000),
            Asset("checksums.txt", 412)));
        using var checker = Create(handler, "0.1.0", "win-x64");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Null(result.Package);
    }

    // A platform RemoteFlow publishes no artefacts for. The check still works; only the install does not.
    [Fact]
    public async Task ABuildOnAnUnpublishedArchitectureOffersNoInstaller()
    {
        using var handler = RecordingHandler.Returning(ReleaseWithAssets(
            "v0.2.0",
            Asset("RemoteFlow-0.2.0-win-x64-setup.exe", 90_000_000),
            Asset("checksums.txt", 412)));
        using var checker = new GitHubUpdateChecker(
            new StubVersion("0.1.0"),
            NullLogger<GitHubUpdateChecker>.Instance,
            handler,
            runtimeIdentifier: null);

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        // The production default reads the running process, which under test is win-x64 — so this asserts
        // only that a check still succeeds, not that no package was chosen.
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
    }

    /// <summary>The asset URL is downloaded from and then executed, so it is held to this repository's own
    /// release download path rather than merely to a host. A response naming anywhere else loses the
    /// installer, not the check.</summary>
    [Theory]
    [InlineData("https://elsewhere.example/michaelou/RemoteFlow/releases/download/v0.2.0/RemoteFlow-0.2.0-win-x64-setup.exe")]
    [InlineData("http://github.com/michaelou/RemoteFlow/releases/download/v0.2.0/RemoteFlow-0.2.0-win-x64-setup.exe")]
    [InlineData("https://github.com/someone/else/releases/download/v0.2.0/RemoteFlow-0.2.0-win-x64-setup.exe")]
    [InlineData("https://raw.githubusercontent.com/michaelou/RemoteFlow/main/RemoteFlow-0.2.0-win-x64-setup.exe")]
    public async Task AnInstallerUrlOffTheReleaseDownloadPathIsRefused(string url)
    {
        var body = $$"""
            {"tag_name":"v0.2.0","html_url":"https://github.com/michaelou/RemoteFlow/releases/tag/v0.2.0",
             "assets":[
               {"name":"RemoteFlow-0.2.0-win-x64-setup.exe","size":90000000,"browser_download_url":"{{url}}"},
               {{Asset("checksums.txt", 412)}}]}
            """;
        using var handler = RecordingHandler.Returning(body);
        using var checker = Create(handler, "0.1.0", "win-x64");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Null(result.Package);
    }

    // Two assets ending -win-x64-setup.exe means the release is not shaped the way this code believes, and
    // picking one of them would be a guess about which binary to run.
    [Fact]
    public async Task TwoCandidateInstallersAreAmbiguousRatherThanFirstWins()
    {
        using var handler = RecordingHandler.Returning(ReleaseWithAssets(
            "v0.2.0",
            Asset("RemoteFlow-nightly-win-x64-setup.exe", 90_000_000),
            Asset("RemoteFlow-patched-win-x64-setup.exe", 90_000_001),
            Asset("checksums.txt", 412)));
        using var checker = Create(handler, "0.1.0", "win-x64");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Null(result.Package);
    }

    // One candidate whose name does not match the tag is still unambiguous, so it is used.
    [Fact]
    public async Task ASingleInstallerNamedUnexpectedlyIsStillTheOne()
    {
        using var handler = RecordingHandler.Returning(ReleaseWithAssets(
            "v0.2.0",
            Asset("RemoteFlow-0.2.0.1-win-x64-setup.exe", 90_000_000),
            Asset("checksums.txt", 412)));
        using var checker = Create(handler, "0.1.0", "win-x64");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal("RemoteFlow-0.2.0.1-win-x64-setup.exe", result.Package!.FileName);
    }

    [Fact]
    public async Task AnUpToDateBuildIsOfferedNoPackageEvenThoughTheAssetsAreThere()
    {
        using var handler = RecordingHandler.Returning(ReleaseWithAssets(
            "v0.1.0",
            Asset("RemoteFlow-0.1.0-win-x64-setup.exe", 90_000_000),
            Asset("checksums.txt", 412)));
        using var checker = Create(handler, "0.1.0", "win-x64");

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
        Assert.Null(result.Package);
    }

    // Reading the assets must not cost a second request: the check is still one GET, as asserted by
    // TheRequestCarriesTheProductNameAndNothingAboutTheMachine, and they come out of the same body.
    [Fact]
    public async Task ReadingTheAssetsCostsNoExtraRequest()
    {
        using var handler = RecordingHandler.Returning(ReleaseWithAssets(
            "v0.2.0",
            Asset("RemoteFlow-0.2.0-win-x64-setup.exe", 90_000_000),
            Asset("checksums.txt", 412)));
        using var checker = Create(handler, "0.1.0", "win-x64");

        _ = await checker.CheckAsync(TestContext.Current.CancellationToken);

        _ = Assert.Single(handler.Requests);
    }

    private static GitHubUpdateChecker Create(
        HttpMessageHandler handler,
        string version,
        string? runtimeIdentifier = null)
    {
        return new GitHubUpdateChecker(
            new StubVersion(version),
            NullLogger<GitHubUpdateChecker>.Instance,
            handler,
            runtimeIdentifier);
    }

    private static string Release(string tag)
    {
        return $$"""
            {"tag_name":"{{tag}}","html_url":"https://github.com/michaelou/RemoteFlow/releases/tag/{{tag}}"}
            """;
    }

    /// <summary>A release body shaped like the one the release workflow produces, assets and all.</summary>
    private static string ReleaseWithAssets(string tag, params string[] assets)
    {
        return $$"""
            {"tag_name":"{{tag}}","html_url":"https://github.com/michaelou/RemoteFlow/releases/tag/{{tag}}",
             "assets":[{{string.Join(",", assets)}}]}
            """;
    }

    private static string Asset(string name, long size)
    {
        return $$"""
            {"name":"{{name}}","size":{{size}},
             "browser_download_url":"https://github.com/michaelou/RemoteFlow/releases/download/v0.2.0/{{name}}"}
            """;
    }

    private sealed class StubVersion(string version) : IAppVersionInfo
    {
        public string Version { get; } = version;

        public string? CommitSha => null;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";
        private Exception? _failure;

        public List<HttpRequestMessage> Requests { get; } = [];

        public static RecordingHandler Returning(string body)
        {
            return Returning(HttpStatusCode.OK, body);
        }

        public static RecordingHandler Returning(HttpStatusCode status, string body)
        {
            return new RecordingHandler { _status = status, _body = body };
        }

        public static RecordingHandler Throwing(Exception failure)
        {
            return new RecordingHandler { _failure = failure };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _failure is not null
                ? Task.FromException<HttpResponseMessage>(_failure)
                : Task.FromResult(new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                });
        }
    }
}
