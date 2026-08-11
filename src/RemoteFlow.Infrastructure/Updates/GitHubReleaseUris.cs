namespace RemoteFlow.Infrastructure.Updates;

/// <summary>Everything RemoteFlow is willing to believe about a URL that arrived in a network response.
///
/// Three rules rather than one, because the consequences differ by an order of magnitude. A release page
/// URL is handed to the browser and nothing else happens; an asset URL is downloaded and then executed. So
/// the page rule stays as loose as it has always been — GitHub serves pages from subdomains — and the asset
/// rule is tightened to one exact host and this repository's own download path. A response naming anywhere
/// else does not produce a warning or a prompt: it produces no installer to offer.</summary>
public static class GitHubReleaseUris
{
    /// <summary>Where the fallback link goes when a response names no page of its own.</summary>
    public const string ReleasesPageUrl = "https://github.com/michaelou/RemoteFlow/releases";

    private const string _releaseHost = "github.com";

    private const string _assetPathPrefix = "/michaelou/RemoteFlow/releases/download/";

    private const string _storageHostSuffix = ".githubusercontent.com";

    /// <summary>A link the desktop shell is asked to open. Loose on purpose: nothing is executed, and GitHub
    /// serves release pages from subdomains of its own.</summary>
    public static bool IsReleasePage(Uri? url)
    {
        return url is { IsAbsoluteUri: true } &&
            url.Scheme == Uri.UriSchemeHttps &&
            (string.Equals(url.Host, _releaseHost, StringComparison.OrdinalIgnoreCase) ||
                url.Host.EndsWith($".{_releaseHost}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A URL RemoteFlow will download from and then run as a process. The exact host, and a path
    /// under this repository's own releases; a subdomain of github.com is not good enough for something
    /// that ends up executing.</summary>
    public static bool IsReleaseAsset(Uri? url)
    {
        return url is { IsAbsoluteUri: true } &&
            url.Scheme == Uri.UriSchemeHttps &&
            string.Equals(url.Host, _releaseHost, StringComparison.OrdinalIgnoreCase) &&
            // UserInfo would let "https://github.com@elsewhere.example/..." read as github.com to a human.
            url.UserInfo.Length == 0 &&
            url.AbsolutePath.StartsWith(_assetPathPrefix, StringComparison.Ordinal);
    }

    /// <summary>A hop the downloader may follow. An asset URL on github.com answers with a redirect to
    /// GitHub's object storage, and GitHub has moved that host more than once — objects. is the
    /// long-standing one and release-assets. the newer — so the rule is the suffix rather than a pair of
    /// literals that would break a release the day it is renamed again.
    ///
    /// Each hop is checked before it is requested rather than after, so a redirect to somewhere else is
    /// never contacted at all.</summary>
    public static bool IsDownloadHop(Uri? url)
    {
        return url is { IsAbsoluteUri: true } &&
            url.Scheme == Uri.UriSchemeHttps &&
            url.UserInfo.Length == 0 &&
            (string.Equals(url.Host, _releaseHost, StringComparison.OrdinalIgnoreCase) ||
                url.Host.EndsWith(_storageHostSuffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A User-Agent product version cannot contain the characters a MinVer build metadata suffix
    /// can, and a malformed header would fail the request rather than the parse.</summary>
    public static string SanitizeUserAgentVersion(string version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var cleaned = new string([.. version.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-')]);
        return cleaned.Length == 0 ? "0.0.0" : cleaned;
    }
}
