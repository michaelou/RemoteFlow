using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.UI.ViewModels.Storage;
using RemoteFlow.UI.Views.Storage;

namespace RemoteFlow.UI.Services;

/// <summary>What the dialog came back with. "Apply to all" is a scope, not a decision, which is why it
/// rides alongside <see cref="TransferConflictDecision"/> rather than becoming a fourth member of it.
/// </summary>
public sealed record TransferConflictChoice(TransferConflictDecision Decision, bool ApplyToAll);

/// <summary>The Avalonia half of the split: window construction and the UI thread, and nothing else. The
/// same division <c>RemoteEditConflictResolver</c> already uses, so the policy above stays plain
/// <c>[Fact]</c> testable.</summary>
public interface ITransferConflictDialogService
{
    Task<TransferConflictChoice> ShowAsync(
        TransferConflict conflict,
        bool offerApplyToAll,
        CancellationToken cancellationToken = default);
}

/// <summary>Hands out one resolver per user gesture. The object's lifetime <em>is</em> the batch, which
/// is what makes "apply to all" work without a batch identifier on an Application contract.</summary>
public interface ITransferConflictResolverFactory
{
    ITransferConflictResolver Create(int batchSize);
}

public sealed class TransferConflictResolverFactory(
    ITransferConflictDialogService dialogs,
    ISettingsStore settings) : ITransferConflictResolverFactory
{
    public ITransferConflictResolver Create(int batchSize)
    {
        return new BatchTransferConflictResolver(dialogs, settings, batchSize);
    }
}

/// <summary>Resolves every conflict in one gesture, remembering a sticky answer if the user asked for
/// one.
///
/// The count and the sticky answer live on the instance rather than in an <c>AsyncLocal</c>, which cannot
/// work here: <c>QueueAsync</c> fires and forgets, so the gesture's call stack has returned long before
/// the queued transfers run and ask. A <c>BatchId</c> on <see cref="TransferConflict"/> was the other
/// option, and it would change an Application contract to serve a UI affordance.</summary>
public sealed class BatchTransferConflictResolver(
    ITransferConflictDialogService dialogs,
    ISettingsStore settings,
    int batchSize) : ITransferConflictResolver, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TransferConflictDecision? _sticky;

    public async ValueTask<TransferConflictDecision> ResolveAsync(
        TransferConflict conflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);

        // One at a time: three parallel transfers hitting three existing keys must not stack three
        // dialogs, and the second must see the first one's "apply to all".
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sticky is { } already)
            {
                return already;
            }

            var configured = await settings.Get(SettingKeys.StorageConflictDefault, cancellationToken)
                .ConfigureAwait(false);
            if (configured != StorageConflictDefault.Prompt)
            {
                return configured == StorageConflictDefault.Overwrite
                    ? TransferConflictDecision.Overwrite
                    : TransferConflictDecision.Skip;
            }

            // Not offered for a batch of one, where it would mean "apply to this one thing".
            var choice = await dialogs.ShowAsync(conflict, batchSize > 1, cancellationToken)
                .ConfigureAwait(false);
            if (choice.ApplyToAll)
            {
                _sticky = choice.Decision;
            }

            return choice.Decision;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }
}

public sealed class TransferConflictDialogService : ITransferConflictDialogService
{
    public async Task<TransferConflictChoice> ShowAsync(
        TransferConflict conflict,
        bool offerApplyToAll,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(conflict, offerApplyToAll));
    }

    private static async Task<TransferConflictChoice> ShowCoreAsync(
        TransferConflict conflict,
        bool offerApplyToAll)
    {
        if (global::Avalonia.Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime { MainWindow.IsVisible: true } desktop)
        {
            return new TransferConflictChoice(TransferConflictDecision.Cancel, ApplyToAll: false);
        }

        var dialog = new TransferConflictDialog(
            new TransferConflictDialogViewModel(conflict, offerApplyToAll));
        await dialog.ShowDialog(desktop.MainWindow);
        return dialog.Choice;
    }
}
