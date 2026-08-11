using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;

namespace RemoteFlow.Infrastructure.Updates;

/// <summary>Fetches a release installer and proves it is the file the release published.
///
/// The proof is the whole point, so it is arranged to be impossible to skip. The digest is fetched first,
/// before the large download, so a release with no usable hash costs nothing and fails early. The bytes are
/// hashed as they are written rather than in a second pass. And the file is written under a
/// <c>.partial</c> name and only renamed once the hash matches, so a file that has not been verified never
/// exists under a name anything would run.
///
/// Redirects are followed by hand rather than by the handler, because an asset URL on github.com answers
/// with a redirect to object storage and the point of checking a hop is to check it <em>before</em> it is
/// contacted. Automatic redirects would have already made the request by the time this code could look at
/// where it went.</summary>
public sealed class ReleaseAssetDownloader : IDisposable
{
    /// <summary>A ceiling on what will be accepted as an installer. The real ones are around 90 MB; this is
    /// generous enough never to matter and small enough that a wrong URL cannot fill a disk.</summary>
    private const long _maximumInstallerBytes = 512L * 1024 * 1024;

    private const int _maximumRedirects = 5;

    private const int _bufferBytes = 81920;

    private readonly IAppPaths _paths;
    private readonly ILogger<ReleaseAssetDownloader> _logger;
    private readonly HttpClient _http;

    /// <summary>Builds a downloader. <c>handler</c> is supplied by tests; when it is null the downloader
    /// owns one of its own, which is the case in the running application.</summary>
    public ReleaseAssetDownloader(
        IAppVersionInfo version,
        IAppPaths paths,
        ILogger<ReleaseAssetDownloader> logger,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _logger = logger;
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);

        // No overall timeout. An installer is around 90 MB and a slow link is not a failure; the Cancel
        // button and the caller's token are the control, not a stopwatch that would abandon a download
        // twenty minutes in.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "RemoteFlow",
            GitHubReleaseUris.SanitizeUserAgentVersion(version.Version)));
    }

    /// <summary>Where downloaded installers are kept. Under the cache directory RemoteFlow already owns
    /// and already creates, rather than in the system temp directory: a file that may be the only way to
    /// recover from a failed install should not sit somewhere a disk cleanup will take it.</summary>
    public string DownloadDirectory => Path.Combine(_paths.CacheDirectory, "Updates");

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdatePackage package,
        string version,
        IProgress<double>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(version);

        if (package.SizeInBytes > _maximumInstallerBytes)
        {
            return UpdateDownloadResult.Failed(
                $"The release says its installer is {Describe(package.SizeInBytes)}, which is far larger " +
                "than a RemoteFlow installer has ever been. Nothing was downloaded.");
        }

        try
        {
            var expected = await ReadExpectedDigestAsync(package, cancellationToken).ConfigureAwait(false);
            return expected is null
                ? UpdateDownloadResult.Failed(
                    "The checksums.txt published with this release does not list " +
                    $"{package.FileName}, so the download could not have been verified. Nothing was " +
                    "downloaded — open the release page and install it yourself.")
                : await DownloadAndVerifyAsync(package, version, expected, progress, cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(exception, "The update download could not complete.");
            }

            return UpdateDownloadResult.Failed($"The download could not complete: {exception.Message}");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    /// <summary>The digest <c>checksums.txt</c> records for this installer, or null when the file cannot be
    /// read or does not mention it. Fetched before the installer so that a release which could never be
    /// verified costs one small request rather than ninety megabytes.</summary>
    private async Task<string?> ReadExpectedDigestAsync(
        UpdatePackage package,
        CancellationToken cancellationToken)
    {
        using var response = await SendFollowingRedirectsAsync(package.ChecksumsUrl, cancellationToken)
            .ConfigureAwait(false);
        if (response is null || !response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.Content.Headers.ContentLength > Sha256Checksums.MaximumSizeInBytes)
        {
            return null;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[Sha256Checksums.MaximumSizeInBytes + 1];
        var read = await ReadAtMostAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
        if (read > Sha256Checksums.MaximumSizeInBytes)
        {
            // Not the file that was asked for. Whatever it is, it is not going to be parsed.
            return null;
        }

        var content = Encoding.UTF8.GetString(buffer, 0, read);
        return Sha256Checksums.TryFind(content, package.FileName, out var digest) ? digest : null;
    }

    private async Task<UpdateDownloadResult> DownloadAndVerifyAsync(
        UpdatePackage package,
        string version,
        string expectedDigest,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(DownloadDirectory);
        var destination = Path.Combine(DownloadDirectory, package.FileName);
        var partial = destination + ".partial";

        using var response = await SendFollowingRedirectsAsync(package.DownloadUrl, cancellationToken)
            .ConfigureAwait(false);
        if (response is null)
        {
            return UpdateDownloadResult.Failed(
                "The installer download was redirected somewhere other than GitHub, so it was not " +
                "followed. Nothing was downloaded.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return UpdateDownloadResult.Failed(string.Format(
                CultureInfo.CurrentCulture,
                "GitHub answered the installer download with {0} ({1}).",
                (int)response.StatusCode,
                response.StatusCode));
        }

        var total = response.Content.Headers.ContentLength ?? package.SizeInBytes;
        string actualDigest;
        try
        {
            actualDigest = await WriteAsync(response, partial, total, progress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
        {
            TryDelete(partial);
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(
                    "The downloaded {FileName} hashed to {Actual}, but the release publishes {Expected}.",
                    package.FileName,
                    actualDigest,
                    expectedDigest);
            }

            return UpdateDownloadResult.Failed(
                "The download does not match the checksum this release published for it, so it will not " +
                "be run. Nothing was installed and the file has been deleted.");
        }

        // Only now does the file get a name something would execute.
        File.Move(partial, destination, overwrite: true);
        progress?.Report(1);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Verified {FileName} ({Digest}) at {Path}.",
                package.FileName,
                actualDigest,
                destination);
        }

        return UpdateDownloadResult.Verified(new VerifiedUpdate(destination, version, actualDigest));
    }

    /// <summary>Streams the body to <paramref name="path"/> and returns its lower-case SHA-256. The hash is
    /// fed from the same buffer as the write, so the file is never read a second time.</summary>
    private static async Task<string> WriteAsync(
        HttpResponseMessage response,
        string path,
        long total,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[_bufferBytes];
        long written = 0;

        await using (var destination = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, _bufferBytes, useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                written += read;
                if (written > _maximumInstallerBytes)
                {
                    throw new IOException("The installer download was larger than any RemoteFlow installer.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                if (progress is not null && total > 0)
                {
                    progress.Report(Math.Clamp((double)written / total, 0, 1));
                }
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Follows redirects by hand, refusing any hop that is not on GitHub. Returns null when a hop
    /// is refused or there were too many; the caller turns that into a sentence.
    ///
    /// Each candidate is checked before it is requested, so a response pointing somewhere else results in
    /// no connection to that host at all — which also means the signed token GitHub puts on its storage
    /// URLs is never sent anywhere unexpected.</summary>
    private async Task<HttpResponseMessage?> SendFollowingRedirectsAsync(
        Uri url,
        CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; hop <= _maximumRedirects; hop++)
        {
            if (!GitHubReleaseUris.IsDownloadHop(current))
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "An update download was redirected to {Host}, which is not GitHub. It was not followed.",
                        current.Host);
                }

                return null;
            }

            var response = await _http
                .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                return null;
            }

            // A Location header may be relative, and resolving it against the current URL is what makes
            // the host check below meaningful rather than accidental.
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning("An update download exceeded {Count} redirects and was abandoned.", _maximumRedirects);
        }

        return null;
    }

    private static bool IsRedirect(HttpStatusCode status)
    {
        return status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static async Task<int> ReadAtMostAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static string Describe(long bytes)
    {
        return string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024.0 * 1024.0):0} MB");
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(exception, "The incomplete download at {Path} could not be deleted.", path);
            }
        }
    }
}
