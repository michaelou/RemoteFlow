namespace RemoteFlow.Application.Abstractions.Storage;

/// <summary>Where a local browser pane was last pointed, remembered across launches.
///
/// One memory, shared: the Storage page's local pane and the SFTP page's local pane read and write the
/// same value, so walking to a folder on one page and switching to the other lands where you were rather
/// than back at the home directory. That is the whole point of remembering it at all — two independent
/// memories would be indistinguishable from none on the page you did not use last.</summary>
public interface ILocalFolderMemory
{
    /// <summary>The remembered folder, or null when there is none or it has since been deleted or
    /// unmounted. A caller that gets null opens the source's own root.</summary>
    Task<string?> RecallAsync(CancellationToken cancellationToken = default);

    Task RememberAsync(string path, CancellationToken cancellationToken = default);
}
