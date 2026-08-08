using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.UI.Services;

namespace RemoteFlow.UI.ViewModels.Security;

public sealed partial class TrustedKeysViewModel(
    IHostKeyStore store,
    IKnownHostsImportService importer,
    IConfirmationDialogService confirmation) : ObservableObject
{
    private readonly IHostKeyStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IKnownHostsImportService _importer = importer ?? throw new ArgumentNullException(nameof(importer));
    private readonly IConfirmationDialogService _confirmation = confirmation ?? throw new ArgumentNullException(nameof(confirmation));
    private IReadOnlyList<HostKey> _allKeys = [];
    private KnownHostsImportPreview? _preview;

    public ObservableCollection<TrustedKeyItemViewModel> Keys { get; } = [];

    public ObservableCollection<KnownHostImportEntry> ImportPreview { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string KnownHostsPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".ssh",
        "known_hosts");

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanApplyImport { get; set; }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _allKeys = await _store.ListAsync(cancellationToken).ConfigureAwait(true);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task RevokeAsync(TrustedKeyItemViewModel? item, CancellationToken cancellationToken)
    {
        if (item is null || item.TrustState == HostKeyTrust.Revoked ||
            !await _confirmation.ConfirmAsync(
                "Revoke trusted key?",
                $"Future connections matching {item.DisplayHost} will be refused until the key is deleted or replaced.",
                "Revoke key",
                cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        _ = item.Source.SetTrust(HostKeyTrust.Revoked, item.Source.Source, item.Source.Comment);
        await _store.UpdateAsync(item.Source, cancellationToken).ConfigureAwait(true);
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync(TrustedKeyItemViewModel? item, CancellationToken cancellationToken)
    {
        if (item is null || !await _confirmation.ConfirmAsync(
                "Delete trusted key?",
                $"Delete the saved key for {item.DisplayHost}? The next strict connection will be refused as unknown.",
                "Delete key",
                cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        await _store.DeleteAsync(item.Id, cancellationToken).ConfigureAwait(true);
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PreviewImportAsync(CancellationToken cancellationToken)
    {
        try
        {
            _preview = await _importer.PreviewAsync(KnownHostsPath, cancellationToken).ConfigureAwait(true);
            ImportPreview.Clear();
            foreach (var entry in _preview.Entries)
            {
                ImportPreview.Add(entry);
            }
            CanApplyImport = ImportPreview.Count > 0;
            StatusMessage = $"Preview: {_preview.Entries.Count} key(s). Review them before applying. {string.Join(' ', _preview.Warnings)}".Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _preview = null;
            ImportPreview.Clear();
            CanApplyImport = false;
            StatusMessage = $"The known_hosts preview could not be read: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task ApplyImportAsync(CancellationToken cancellationToken)
    {
        if (_preview is null || _preview.Entries.Count == 0 ||
            !await _confirmation.ConfirmAsync(
                "Import previewed keys?",
                $"Add {_preview.Entries.Count} previewed key(s) to RemoteFlow? The source known_hosts file will remain unchanged.",
                "Apply import",
                cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        var result = await _importer.ApplyAsync(_preview, cancellationToken).ConfigureAwait(true);
        StatusMessage = $"Imported {result.Added} key(s); skipped {result.Skipped} existing key(s). The OpenSSH file was not changed.";
        _preview = null;
        ImportPreview.Clear();
        CanApplyImport = false;
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        Keys.Clear();
        foreach (var key in _allKeys.Where(key => query.Length == 0 ||
                     key.Host.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     key.KeyAlgorithm.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     key.Sha256Fingerprint.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            Keys.Add(new(key));
        }
    }
}

public sealed class TrustedKeyItemViewModel(HostKey source)
{
    public HostKey Source { get; } = source;
    public Guid Id => Source.Id;
    public string DisplayHost => Source.Host.StartsWith("|1|", StringComparison.Ordinal)
        ? "Hashed hostname"
        : $"{Source.Host}:{Source.Port}";
    public string KeyAlgorithm => Source.KeyAlgorithm;
    public string Sha256Fingerprint => Source.Sha256Fingerprint;
    public HostKeyTrust TrustState => Source.TrustState;
    public string SourceLabel => Source.Source.ToString();
    public bool IsHashed => Source.Host.StartsWith("|1|", StringComparison.Ordinal);
}
