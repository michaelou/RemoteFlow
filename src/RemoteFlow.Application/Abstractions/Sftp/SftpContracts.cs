namespace RemoteFlow.Application.Abstractions.Sftp;

public enum SftpError
{
    PermissionDenied = 1,
    NotFound = 2,
    NotDirectory = 3,
    AlreadyExists = 4,
    QuotaExceeded = 5,
    NotSupported = 6,
    Cancelled = 7,
    ConnectionLost = 8,
    InvalidPath = 9,
    Unknown = 10,
}

public sealed record SftpFailure(SftpError Error, string Message);

public class SftpResult
{
    protected SftpResult(SftpFailure? failure)
    {
        FailureValue = failure;
    }

    public bool IsSuccess => FailureValue is null;

    public bool IsFailure => FailureValue is not null;

    public SftpFailure Failure => FailureValue ??
        throw new InvalidOperationException("A successful SFTP result has no failure.");

    protected SftpFailure? FailureValue { get; }

    public static SftpResult Success()
    {
        return new(null);
    }

    public static SftpResult Fail(SftpError error, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(new SftpFailure(error, message));
    }
}

public sealed class SftpResult<T> : SftpResult
{
    private SftpResult(T value) : base(null)
    {
        SuccessfulValue = value;
    }

    private SftpResult(SftpFailure failure) : base(failure) { }

    public T Value => IsSuccess
        ? SuccessfulValue!
        : throw new InvalidOperationException("A failed SFTP result has no value.");

    private T? SuccessfulValue { get; }

#pragma warning disable CA1000 // Result factories intentionally live on the result type.
    public static SftpResult<T> Success(T value)
    {
        return new(value);
    }

    public static new SftpResult<T> Fail(SftpError error, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(new SftpFailure(error, message));
    }
#pragma warning restore CA1000
}

public sealed record RemoteFileInfo(
    string Name,
    string FullPath,
    long Size,
    DateTimeOffset ModifiedTime,
    UnixFileMode Mode,
    string Owner,
    string Group,
    bool IsDirectory,
    bool IsSymlink,
    string? SymlinkTarget);

public interface ISftpService : IAsyncDisposable
{
    Task<SftpResult<IReadOnlyList<RemoteFileInfo>>> ListAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<SftpResult<RemoteFileInfo?>> StatAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<SftpResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task<SftpResult> RenameAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<SftpResult> DeleteAsync(
        string path,
        bool recursive,
        CancellationToken cancellationToken = default);

    Task<SftpResult> SetPermissionsAsync(
        string path,
        UnixFileMode mode,
        CancellationToken cancellationToken = default);

    Task<SftpResult<string>> GetRealPathAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<SftpResult<Stream>> OpenReadAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<SftpResult<Stream>> OpenWriteAsync(
        string path,
        CancellationToken cancellationToken = default);
}

public static class SftpPath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = path.Replace('\\', '/');
        var absolute = canonical[0] == '/';
        var homeRelative = canonical.Equals("~", StringComparison.Ordinal) ||
            canonical.StartsWith("~/", StringComparison.Ordinal);
        var segments = new List<string>();

        foreach (var segment in canonical.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] is not ".." and not "~")
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                else if (!absolute && !homeRelative)
                {
                    segments.Add(segment);
                }

                continue;
            }

            segments.Add(segment);
        }

        var normalized = string.Join('/', segments);
        return absolute
            ? normalized.Length == 0 ? "/" : "/" + normalized
            : normalized.Length == 0 ? "." : normalized;
    }

    public static string Combine(string parent, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Normalize(parent.TrimEnd('/', '\\') + "/" + name);
    }

    public static string GetName(string path)
    {
        var normalized = Normalize(path);
        if (normalized == "/")
        {
            return "/";
        }

        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }

    public static string ToShellLiteral(string path)
    {
        return "'" + Normalize(path).Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    }

    public static string FormatMode(UnixFileMode mode)
    {
        return Convert.ToString((int)mode, 8).PadLeft(4, '0');
    }
}
