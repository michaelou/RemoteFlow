using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.Application.Services.Backup;

/// <summary>Answers every "the destination already exists" question with overwrite. Both transfer engines
/// report a Conflict rather than transferring when no resolver is supplied, and a Conflict surfacing as a
/// failed backup would be a confusing way to discover that. Collisions should be impossible anyway — the
/// archive name carries a random nonce — so the only file this can overwrite is one of ours.</summary>
internal sealed class AlwaysOverwriteConflictResolver : ITransferConflictResolver
{
    public static AlwaysOverwriteConflictResolver Instance { get; } = new();

    public ValueTask<TransferConflictDecision> ResolveAsync(
        TransferConflict conflict,
        CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(TransferConflictDecision.Overwrite);
    }
}
