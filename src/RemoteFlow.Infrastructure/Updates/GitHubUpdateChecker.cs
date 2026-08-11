using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
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
    public const string ReleasesPageUrl = GitHubReleaseUris.ReleasesPageUrl;

    private const string _latestReleaseEndpoint =
        "https://api.github.com/repos/michaelou/RemoteFlow/releases/latest";

    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    private readonly IAppVersionInfo _version;
    private readonly ILogger<GitHubUpdateChecker> _logger;
    private readonly HttpClient _http;
    private readonly string? _runtimeIdentifier;

    /// <summary>Builds a checker. <c>handler</c> and <c>runtimeIdentifier</c> are supplied by tests; when
    /// they are null the checker owns a handler of its own and reads the architecture of the running
    /// process, which is the case in the running application.</summary>
    public GitHubUpdateChecker(
        IAppVersionInfo version,
        ILogger<GitHubUpdateChecker> logger,
        HttpMessageHandler? handler = null,
        string? runtimeIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(logger);
        _version = version;
        _logger = logger;
        _runtimeIdentifier = runtimeIdentifier ?? CurrentRuntimeIdentifier();
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = _timeout;
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        // GitHub rejects a request without one. "RemoteFlow/0.1.0" names the software and nothing else.
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "RemoteFlow",
            GitHubReleaseUris.SanitizeUserAgentVersion(version.Version)));
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
            ? UpdateCheckResult.UpdateAvailable(latest.ToString(), page, SelectPackage(release, latest))
            : UpdateCheckResult.UpToDate(latest.ToString(), page);
    }

    /// <summary>The installer this build could install over itself, or null.
    ///
    /// Null is not a failure and never becomes one: a release with no usable asset still reports that a
    /// newer version exists and still links to its page. All this decides is whether the button appears,
    /// and it is better for it not to appear than to appear and fail after being pressed.</summary>
    private UpdatePackage? SelectPackage(JsonElement release, SemanticVersion latest)
    {
        if (_runtimeIdentifier is null ||
            !release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Without a published hash there is nothing to check the download against, and RemoteFlow does not
        // run an installer it cannot verify. So no checksums.txt means no package at all, checked first
        // because it is the cheapest way to rule the whole release out.
        var checksums = MatchAsset(assets, name => string.Equals(name, "checksums.txt", StringComparison.Ordinal));
        if (checksums is null)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Release {Version} publishes no checksums.txt, so it cannot be installed from here.",
                    latest);
            }

            return null;
        }

        // The name the release workflow produces. Matching it exactly cross-checks the tag against the
        // asset names for free; the suffix is the fallback, and only when it is unambiguous, because two
        // candidates mean the release is not shaped the way this code believes.
        var exactName = $"RemoteFlow-{latest}-{_runtimeIdentifier}-setup.exe";
        var suffix = $"-{_runtimeIdentifier}-setup.exe";
        var installer =
            MatchAsset(assets, name => string.Equals(name, exactName, StringComparison.OrdinalIgnoreCase)) ??
            MatchAsset(assets, name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase), unique: true);

        if (installer is null)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Release {Version} publishes no {Identifier} installer that can be identified.",
                    latest,
                    _runtimeIdentifier);
            }

            return null;
        }

        return new UpdatePackage(installer.Name, installer.Url, installer.Size, checksums.Url);
    }

    /// <summary>The one asset matching a predicate whose download URL is on this repository's release
    /// download path. <paramref name="unique"/> refuses a second match rather than taking the first.</summary>
    private static ReleaseAsset? MatchAsset(
        JsonElement assets,
        Func<string, bool> matches,
        bool unique = false)
    {
        ReleaseAsset? found = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = ReadString(asset, "name");
            if (name.Length == 0 || !matches(name))
            {
                continue;
            }

            // The URL is about to be downloaded from and, for the installer, run. It came out of a network
            // response, so it is held to the release download path rather than merely to a host.
            if (!Uri.TryCreate(ReadString(asset, "browser_download_url"), UriKind.Absolute, out var url) ||
                !GitHubReleaseUris.IsReleaseAsset(url))
            {
                continue;
            }

            if (!unique)
            {
                return new ReleaseAsset(name, url, ReadSize(asset));
            }

            if (found is not null)
            {
                return null;
            }

            found = new ReleaseAsset(name, url, ReadSize(asset));
        }

        return found;
    }

    /// <summary>Only used to show a size and to size the progress bar, so a release that omits it costs
    /// nothing: the downloader falls back to the response's own Content-Length.</summary>
    private static long ReadSize(JsonElement asset)
    {
        return asset.TryGetProperty("size", out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out var size) &&
            size > 0
            ? size
            : 0;
    }

    /// <summary>The runtime identifier whose artefacts this process should install, or null on a platform
    /// RemoteFlow publishes none for.
    ///
    /// This is the identifier the running build was published for, not the machine's — an x64 build running
    /// under emulation on an Arm device stays on the x64 track. Moving somebody between architectures is a
    /// decision, and an update they did not read is not the place to make it for them.</summary>
    private static string? CurrentRuntimeIdentifier()
    {
        var identifier = RuntimeInformation.RuntimeIdentifier;
        return identifier is "win-x64" or "win-arm64" ? identifier : null;
    }

    private sealed record ReleaseAsset(string Name, Uri Url, long Size);

    /// <summary>The link the user is offered. It comes out of a network response, so it is checked before
    /// it can become something the desktop shell is asked to open; a response naming anywhere else falls
    /// back to the releases page this build was compiled with.</summary>
    private static Uri ReleasePageUrl(JsonElement release)
    {
        var fallback = new Uri(ReleasesPageUrl);
        var candidate = ReadString(release, "html_url");
        return Uri.TryCreate(candidate, UriKind.Absolute, out var url) && GitHubReleaseUris.IsReleasePage(url)
            ? url
            : fallback;
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
}
