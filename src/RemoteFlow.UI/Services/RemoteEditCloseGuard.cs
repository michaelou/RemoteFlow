using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.UI.Services;

public sealed class RemoteEditCloseGuard(IConfirmationDialogService confirmation) : IRemoteEditCloseGuard
{
    public Task<bool> ConfirmDiscardUnsavedChangesAsync(
        string remotePath,
        CancellationToken cancellationToken = default)
    {
        return confirmation.ConfirmAsync(
            "Unsaved remote edit",
            $"'{remotePath}' has local changes that were not uploaded. Close it and discard the local copy?",
            "Discard local copy",
            cancellationToken);
    }
}
