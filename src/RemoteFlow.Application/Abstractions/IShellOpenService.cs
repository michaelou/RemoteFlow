namespace RemoteFlow.Application.Abstractions;

/// <summary>The outcome of handing something to the desktop shell. Failures are reported rather than
/// thrown: a file manager that will not start is worth a sentence on screen, not a crash.</summary>
public sealed record ShellOpenResult(bool Succeeded, string? ErrorMessage = null)
{
    public static ShellOpenResult Success { get; } = new(true);

    public static ShellOpenResult Failure(string message)
    {
        return new(false, message);
    }
}

/// <summary>Opens a folder in the desktop file manager, or a link in the browser.
///
/// Distinct from <see cref="IFileRevealService"/>, which selects one file inside its folder after a
/// transfer. This one opens a directory RemoteFlow owns — logs, data — and is what the about box uses to
/// get someone to their own files without telling them a path to type.</summary>
public interface IShellOpenService
{
    Task<ShellOpenResult> OpenFolderAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Opens an absolute <c>http</c> or <c>https</c> URL. Any other scheme is refused: handing an
    /// arbitrary URI to the shell is handing it an arbitrary command on some platforms.</summary>
    Task<ShellOpenResult> OpenUrlAsync(Uri url, CancellationToken cancellationToken = default);
}
