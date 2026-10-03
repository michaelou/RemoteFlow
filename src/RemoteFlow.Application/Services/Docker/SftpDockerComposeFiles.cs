using System.Text;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Application.Services.Docker;

/// <summary><see cref="IDockerComposeFiles"/> over the SFTP channel of the Docker page's connection. SFTP
/// rather than <c>cat</c> and a here-document, because a file goes through it as bytes: no quoting, no
/// command-line length limit, and nothing a shell could read as a command.
///
/// A save writes a temporary file beside the real one and moves it into place, so a dropped connection
/// leaves either the old file or the new one and never half of each; the old file's permissions are
/// kept.</summary>
public sealed class SftpDockerComposeFiles(ISftpService sftp) : IDockerComposeFiles, IAsyncDisposable
{
    /// <summary>Far beyond any compose file a person edits by hand; a file this big is not one.</summary>
    public const long MaxFileSize = 1024 * 1024;

    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly ISftpService _sftp = sftp ?? throw new ArgumentNullException(nameof(sftp));

    public async Task<DockerResult<string>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Validate(path) is { } invalid)
        {
            return DockerResult<string>.Fail(invalid);
        }

        var stat = await _sftp.StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (stat.IsFailure)
        {
            return Unavailable<string>(path, "read", stat.Failure);
        }

        if (stat.Value is null)
        {
            return DockerResult<string>.Fail(DockerError.FileUnavailable, $"'{path}' does not exist on the server.");
        }

        if (stat.Value.IsDirectory)
        {
            return DockerResult<string>.Fail(DockerError.FileUnavailable, $"'{path}' is a folder, not a compose file.");
        }

        if (stat.Value.Size > MaxFileSize)
        {
            return DockerResult<string>.Fail(
                DockerError.FileUnavailable,
                $"'{path}' is larger than {MaxFileSize / 1024} KB, too large to be a compose file to edit here.");
        }

        var opened = await _sftp.OpenReadAsync(path, cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            return Unavailable<string>(path, "read", opened.Failure);
        }

        await using (opened.Value.ConfigureAwait(false))
        using (var reader = new StreamReader(opened.Value, _utf8, detectEncodingFromByteOrderMarks: true))
        {
            return DockerResult<string>.Success(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        }
    }

    public async Task<DockerResult> WriteAsync(
        string path,
        string text,
        string? expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Validate(path) is { } invalid)
        {
            return DockerResult.Fail(invalid.Error, invalid.Message);
        }

        var unchanged = expected is null
            ? await EnsureFreeAsync(path, cancellationToken).ConfigureAwait(false)
            : await EnsureUnchangedAsync(path, expected, cancellationToken).ConfigureAwait(false);
        if (unchanged is not null)
        {
            return unchanged;
        }

        var temporary = $"{path}.remoteflow-{Guid.NewGuid():N}.part";
        var opened = await _sftp.OpenWriteAsync(temporary, cancellationToken).ConfigureAwait(false);
        if (opened.IsFailure)
        {
            return Unavailable<string>(path, "saved", opened.Failure);
        }

        try
        {
            await using (opened.Value.ConfigureAwait(false))
            {
                var bytes = _utf8.GetBytes(text);
                await opened.Value.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await opened.Value.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var published = await SftpPublisher.PublishAsync(_sftp, temporary, path, cancellationToken).ConfigureAwait(false);
            return published.IsFailure ? Unavailable<string>(path, "saved", published.Failure) : DockerResult.Success();
        }
        finally
        {
            // Gone already once the publish succeeded; this only clears up after a failure.
            _ = await _sftp.DeleteAsync(temporary, recursive: false, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync()
    {
        return _sftp.DisposeAsync();
    }

    /// <summary>Absolute paths only: SFTP would resolve a relative one against the home folder, and
    /// <c>docker compose -f</c> against wherever the command happens to run.</summary>
    private static DockerFailure? Validate(string? path)
    {
        return DockerCli.IsValidComposeFile(path) && path![0] == '/' && !path.EndsWith('/')
            ? null
            : new DockerFailure(
                DockerError.InvalidArgument,
                $"'{path}' is not a full path to a file on the server, such as /srv/app/compose.yaml.");
    }

    private async Task<DockerResult?> EnsureUnchangedAsync(string path, string expected, CancellationToken cancellationToken)
    {
        var stat = await _sftp.StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (stat is { IsSuccess: true, Value: null })
        {
            return DockerResult.Fail(DockerError.FileChanged, $"'{path}' was deleted on the server after it was opened.");
        }

        var current = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return current.IsFailure
            ? current
            : string.Equals(current.Value, expected, StringComparison.Ordinal)
                ? null
                : DockerResult.Fail(DockerError.FileChanged, $"'{path}' was changed on the server after it was opened.");
    }

    /// <summary>A new file must not land on an existing one, and its folders are made as needed — the
    /// usual case is a new project in a folder of its own.</summary>
    private async Task<DockerResult?> EnsureFreeAsync(string path, CancellationToken cancellationToken)
    {
        var stat = await _sftp.StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (stat.IsFailure)
        {
            return Unavailable<string>(path, "saved", stat.Failure);
        }

        if (stat.Value is not null)
        {
            return DockerResult.Fail(DockerError.FileChanged, $"'{path}' already exists. Open it from its project instead.");
        }

        var missing = new Stack<string>();
        for (var folder = Parent(path); folder is not null; folder = Parent(folder))
        {
            var found = await _sftp.StatAsync(folder, cancellationToken).ConfigureAwait(false);
            if (found.IsFailure)
            {
                return Unavailable<string>(folder, "checked", found.Failure);
            }

            if (found.Value is not null)
            {
                if (!found.Value.IsDirectory)
                {
                    return DockerResult.Fail(DockerError.FileUnavailable, $"'{folder}' is a file, so it cannot hold '{path}'.");
                }

                break;
            }

            missing.Push(folder);
        }

        while (missing.Count > 0)
        {
            var folder = missing.Pop();
            var created = await _sftp.CreateDirectoryAsync(folder, cancellationToken).ConfigureAwait(false);
            if (created.IsFailure && created.Failure.Error != SftpError.AlreadyExists)
            {
                return Unavailable<string>(folder, "created", created.Failure);
            }
        }

        return null;
    }

    private static string? Parent(string path)
    {
        var index = path.TrimEnd('/').LastIndexOf('/');
        return index <= 0 ? null : path[..index];
    }

    /// <summary><paramref name="done"/> is what failed, in the past tense: "read", "saved".</summary>
    private static DockerResult<T> Unavailable<T>(string path, string done, SftpFailure failure)
    {
        var reason = failure.Error == SftpError.PermissionDenied
            ? "the SSH account does not have permission"
            : failure.Message.TrimEnd('.');
        return DockerResult<T>.Fail(DockerError.FileUnavailable, $"'{path}' could not be {done}: {reason}.");
    }
}
