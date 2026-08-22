using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.UI.ViewModels.Storage;

/// <summary>What the transfer conflict dialog shows. Deliberately thin: the decision itself is policy and
/// lives in <c>BatchTransferConflictResolver</c>, which has no Avalonia in it.</summary>
public sealed class TransferConflictDialogViewModel(TransferConflict conflict, bool offerApplyToAll)
{
    public string SourcePath { get; } = conflict is not null
        ? conflict.SourcePath
        : throw new ArgumentNullException(nameof(conflict));

    public string DestinationPath { get; } = conflict.DestinationPath;

    public string DirectionText { get; } = conflict.Direction == TransferDirection.Upload
        ? "Uploading"
        : "Downloading";

    public string ExistingSizeText { get; } = conflict.ExistingSize is { } size
        ? $"{size:N0} bytes"
        : "Unknown size";

    /// <summary>Never offered for a batch of one, where "apply to all" would mean "apply to this one
    /// thing" and read as a trick question.</summary>
    public bool OfferApplyToAll { get; } = offerApplyToAll;

    public bool ApplyToAll { get; set; }
}
