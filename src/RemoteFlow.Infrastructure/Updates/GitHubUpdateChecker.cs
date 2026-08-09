using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;

namespace RemoteFlow.Infrastructure.Updates;

/// <summary>Asks GitHub for the newest published release and compares it with the running build.
///
/// One unauthenticated GET to <c>releases/latest</c>, which by definition skips drafts and prereleases —
/// so a release candidate is never offered to someone running a stable build. Nothing about the machine
/// is sent: the request carries no identifier RemoteFlow invented, only the product name and version in
/// the User-Agent header, which GitHub requires a value for and which names the software rather than the
/// person running it.
///
/// Every failure returns <see cref="UpdateCheckOutcome.Failed"/> with a sentence to put on screen. There
/// is no retry and no queue: an update check that could not reach the network is not worth remembering,
/// and the button is right there.</summary>
public sealed class GitHubUpdateChecker : IUpdateChecker, IDisposable
{
    /// <summary>Where the fallback link goes when the response names no page of its own.</summary>
    public const string ReleasesPageUrl = "https://github.com/michaelou/RemoteFlow/releases";

    private const string _latestReleaseEndpoint =
        "https://api.github.com/repos/michaelou/RemoteFlow/releases/latest";

    private const string _releaseHost = "github.com";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    private readonly IAppVersionInfo _version;
    private readonly ILogger<GitHubUpdateChecker> _logger;
    private readonly HttpClient _http;

    /// <summary>Builds a checker. <c>handler</c> is supplied by tests; when it is null the checker owns a
    /// handler of its own, which is the case in the running application.</summary>
    public GitHubUpdateChecker(
        IAppVersionInfo version,
        ILogger<GitHubUpdateChecker> logger,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(logger);
        _version = version;
        _logger = logger;
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = _timeout;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // GitHub rejects a request without one. "RemoteFlow/0.1.0" names the software and nothing else.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "RemoteFlow",
            SanitizeUserAgentVersion(version.Version)));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!SemanticVersion.TryParse(_version.Version, out var current))
        {
            // A build whose own version cannot be read has nothing to compare against, and guessing would
            // mean either nagging every launch or never reporting an update at all.
            return UpdateCheckResult.Failed(
                $"This build reports its version as \"{_version.Version}\", which is not a version number " +
                $"that can be compared. Check {ReleasesPageUrl} yourself.");
        }

        try
        {
            using var response = await _http
                .GetAsync(new Uri(_latestReleaseEndpoint), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            // 404 is the documented answer when a repository has published nothing, and is true of
            // RemoteFlow until the first tag is released. It is not an error.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return UpdateCheckResult.NoReleaseYet();
            }

            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed(DescribeFailure(response.StatusCode));
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return Compare(current, document.RootElement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient reports its own timeout this way rather than as a TimeoutException.
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("The update check timed out after {Seconds} seconds.", _timeout.TotalSeconds);
            }

            return UpdateCheckResult.Failed(
                $"The update check timed out after {_timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(exception, "The update check could not complete.");
            }

            return UpdateCheckResult.Failed($"The update check could not complete: {exception.Message}");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    private UpdateCheckResult Compare(SemanticVersion current, JsonElement release)
    {
        var tag = ReadString(release, "tag_name");
        if (!SemanticVersion.TryParse(tag, out var latest))
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("The newest release is tagged {Tag}, which is not a SemVer version.", tag);
            }

            return UpdateCheckResult.Failed(
                $"The newest release is tagged \"{tag}\", which cannot be compared with this build. " +
                $"Check {ReleasesPageUrl} yourself.");
        }

        var page = ReleasePageUrl(release);
        return latest > current
            ? UpdateCheckResult.UpdateAvailable(latest.ToString(), page)
            : UpdateCheckResult.UpToDate(latest.ToString(), page);
    }

    /// <summary>The link the user is offered. It comes out of a network response, so it is checked to be
    /// an https URL on github.com before it can become something the desktop shell is asked to open; a
    /// response naming anywhere else falls back to the releases page this build was compiled with.</summary>
    private static Uri ReleasePageUrl(JsonElement release)
    {
        var fallback = new Uri(ReleasesPageUrl);
        var candidate = ReadString(release, "html_url");
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var url))
        {
            return fallback;
        }

        var isGitHub = url.Scheme == Uri.UriSchemeHttps &&
            (string.Equals(url.Host, _releaseHost, StringComparison.OrdinalIgnoreCase) ||
                url.Host.EndsWith($".{_releaseHost}", StringComparison.OrdinalIgnoreCase));
        return isGitHub ? url : fallback;
    }

    private static string DescribeFailure(HttpStatusCode status)
    {
        // Unauthenticated calls get sixty an hour per address, which nobody reaches by pressing a button —
        // but a shared address behind one NAT can, and "try later" is the honest answer.
        return status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            ? "GitHub is rate-limiting update checks from this network. Try again later, or see " +
                $"{ReleasesPageUrl}."
            : string.Format(
                CultureInfo.CurrentCulture,
                "GitHub answered the update check with {0} ({1}).",
                (int)status,
                status);
    }

    private static string ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    /// <summary>A User-Agent product version cannot contain the characters a MinVer build metadata suffix
    /// can, and a malformed header would fail the request rather than the parse.</summary>
    private static string SanitizeUserAgentVersion(string version)
    {
        var cleaned = new string([.. version.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-')]);
        return cleaned.Length == 0 ? "0.0.0" : cleaned;
    }
}
