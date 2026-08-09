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

    private static GitHubUpdateChecker Create(HttpMessageHandler handler, string version)
    {
        return new GitHubUpdateChecker(
            new StubVersion(version),
            NullLogger<GitHubUpdateChecker>.Instance,
            handler);
    }

    private static string Release(string tag)
    {
        return $$"""
            {"tag_name":"{{tag}}","html_url":"https://github.com/michaelou/RemoteFlow/releases/tag/{{tag}}"}
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
