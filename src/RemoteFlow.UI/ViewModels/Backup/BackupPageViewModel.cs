using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.UI.Services;
using RemoteFlow.Domain.Enums;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RemoteFlow.UI.ViewModels.Backup;

public sealed class BackupPageViewModel(
    BackupExportViewModel export,
    BackupImportPreviewViewModel import,
    AutomaticBackupSettingsViewModel automatic) : PageViewModel("Backup")
{
    public BackupExportViewModel Export { get; } = export ?? throw new ArgumentNullException(nameof(export));

    public BackupImportPreviewViewModel Import { get; } = import ?? throw new ArgumentNullException(nameof(import));

    public AutomaticBackupSettingsViewModel Automatic { get; } = automatic ?? throw new ArgumentNullException(nameof(automatic));
}

public sealed partial class BackupImportPreviewViewModel(
    IBackupService backupService,
    IFilePickerService filePicker,
    IErrorDialogService errorDialog) : ObservableObject
{
    private readonly IBackupService _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
    private readonly IFilePickerService _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
    private readonly IErrorDialogService _errorDialog = errorDialog ?? throw new ArgumentNullException(nameof(errorDialog));

    public IReadOnlyList<MergeStrategy> Strategies { get; } = Enum.GetValues<MergeStrategy>();

    public IReadOnlyList<MergeConflictPolicy> ConflictPolicies { get; } = Enum.GetValues<MergeConflictPolicy>();

    [ObservableProperty]
    public partial string SelectedPath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CountsSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CredentialSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string MergeDescription { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ReplaceDescription { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> ConflictDescriptions { get; private set; } = [];

    [ObservableProperty]
    public partial MergeStrategy SelectedStrategy { get; set; } = MergeStrategy.Merge;

    [ObservableProperty]
    public partial MergeConflictPolicy SelectedConflictPolicy { get; set; } = MergeConflictPolicy.PreferLocal;

    [ObservableProperty]
    public partial string ReplaceConfirmation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CredentialPassphrase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ApplySummary { get; private set; } = string.Empty;

    [RelayCommand]
    private async Task InspectAsync()
    {
        var paths = await _filePicker.PickUploadPathsAsync().ConfigureAwait(true);
        var path = paths.Count == 0 ? null : paths[0];
        if (path is null)
        {
            return;
        }

        try
        {
            var inspection = await _backupService.InspectAsync(path).ConfigureAwait(true);
            SelectedPath = path;
            CountsSummary = FormatCounts(inspection.Counts);
            CredentialSummary = inspection.ContainsCredentials
                ? "This archive contains encrypted credentials; a passphrase will be required when applying it."
                : "This archive does not contain credential secrets.";
            MergeDescription = inspection.MergePreview.Description;
            ReplaceDescription = inspection.ReplacePreview.Description;
            ConflictDescriptions = [.. inspection.Conflicts.Select(conflict => conflict.Description)];
        }
        catch (BackupArchiveException exception)
        {
            await _errorDialog.ShowAsync("Backup inspection failed", exception.Message).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPath))
        {
            await _errorDialog.ShowAsync("No backup selected", "Inspect a backup before applying it.").ConfigureAwait(true);
            return;
        }

        try
        {
            var passphrase = CredentialPassphrase.ToCharArray();
            try
            {
                var result = await _backupService.ApplyAsync(new BackupApplyRequest(
                    SelectedPath,
                    SelectedStrategy,
                    SelectedConflictPolicy,
                    ReplaceConfirmation,
                    passphrase)).ConfigureAwait(true);
                ApplySummary = result.Summary;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(passphrase.AsSpan()));
                CredentialPassphrase = string.Empty;
            }
        }
        catch (Exception exception) when (exception is BackupArchiveException or BackupCredentialException or InvalidOperationException)
        {
            await _errorDialog.ShowAsync("Backup import failed", exception.Message).ConfigureAwait(true);
        }
    }

    private static string FormatCounts(BackupEntityCounts counts)
    {
        return $"{counts.Connections} connections, {counts.Folders} folders, {counts.Tags} tags, " +
            $"{counts.Settings} settings, and {counts.HostKeys} host keys";
    }
}

public sealed partial class BackupExportViewModel(
    IBackupService backupService,
    IFilePickerService filePicker,
    IErrorDialogService errorDialog) : ObservableObject, IDisposable
{
    private readonly IBackupService _backupService = backupService ?? throw new ArgumentNullException(nameof(backupService));
    private readonly IFilePickerService _filePicker = filePicker ?? throw new ArgumentNullException(nameof(filePicker));
    private readonly IErrorDialogService _errorDialog = errorDialog ?? throw new ArgumentNullException(nameof(errorDialog));
    private CancellationTokenSource? _cancellation;

    public IReadOnlyList<BackupExportScopeKind> ScopeKinds { get; } = Enum.GetValues<BackupExportScopeKind>();

    public bool CanExportCredentials => _backupService.CanExportCredentials;

    public string CredentialWarning => CanExportCredentials
        ? "A lost passphrase is unrecoverable. Anyone who has the archive and passphrase can recover every included credential."
        : "Encrypted credential export is not available yet; no credential secrets will be included.";

    [ObservableProperty]
    public partial BackupExportScopeKind SelectedScopeKind { get; set; } = BackupExportScopeKind.All;

    [ObservableProperty]
    public partial string? SelectedFolderId { get; set; }

    [ObservableProperty]
    public partial string SelectedConnectionIds { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IncludeSettings { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeHostKeys { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeCredentials { get; set; }

    [ObservableProperty]
    public partial string CredentialPassphrase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AllowWeakPassphrase { get; set; }

    [ObservableProperty]
    public partial bool IsExporting { get; private set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultSummary { get; private set; } = string.Empty;

    [RelayCommand(CanExecute = nameof(CanStartExport))]
    private async Task ExportAsync()
    {
        var folder = await _filePicker.PickDownloadFolderAsync(cancellationToken: default).ConfigureAwait(true);
        if (folder is null)
        {
            return;
        }

        var destination = Path.Combine(folder, $"RemoteFlow-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        _cancellation = new CancellationTokenSource();
        IsExporting = true;
        ResultSummary = string.Empty;
        ExportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        try
        {
            var progress = new Progress<BackupProgress>(item =>
            {
                ProgressPercent = item.Percent;
                ProgressText = item.Stage;
            });
            var passphrase = CredentialPassphrase.ToCharArray();
            try
            {
                var request = new BackupExportRequest(
                    destination,
                    CreateScope(),
                    IncludeSettings,
                    IncludeHostKeys,
                    IncludeCredentials,
                    CredentialPassphrase: passphrase,
                    AllowWeakPassphrase: AllowWeakPassphrase);
                var result = await _backupService.ExportAsync(request, progress, _cancellation.Token).ConfigureAwait(true);
                ResultSummary = result.Summary;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(passphrase.AsSpan()));
                CredentialPassphrase = string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Export cancelled";
        }
        catch (Exception exception) when (exception is BackupArchiveException or BackupCredentialException or ArgumentException or IOException)
        {
            await _errorDialog.ShowAsync("Backup export failed", exception.Message).ConfigureAwait(true);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsExporting = false;
            ExportCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(IsExporting))]
    private void Cancel()
    {
        _cancellation?.Cancel();
    }

    public void Dispose()
    {
        _cancellation?.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool CanStartExport()
    {
        return !IsExporting;
    }

    private BackupExportScope CreateScope()
    {
        return SelectedScopeKind switch
        {
            BackupExportScopeKind.All => BackupExportScope.All,
            BackupExportScopeKind.FolderSubtree when Guid.TryParse(SelectedFolderId, out var folderId) =>
                BackupExportScope.FolderSubtree(folderId),
            BackupExportScopeKind.SelectedConnections => BackupExportScope.SelectedConnections(
                SelectedConnectionIds.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                    .Select(Guid.Parse)),
            _ => throw new ArgumentException("Choose a valid folder or connection selection."),
        };
    }
}
