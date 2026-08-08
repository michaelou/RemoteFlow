using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.UI.ViewModels.Connections;

public sealed partial class SshKeyPickerViewModel(
    ISshKeyService keyService,
    IClipboardService clipboard) : ObservableObject
{
    private readonly ISshKeyService _keyService = keyService ?? throw new ArgumentNullException(nameof(keyService));
    private readonly IClipboardService _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));

    public ObservableCollection<string> RecentKeys { get; } = [];

    [ObservableProperty]
    public partial string? SelectedPath { get; set; }

    [ObservableProperty]
    public partial string? KeyType { get; private set; }

    [ObservableProperty]
    public partial string? Sha256Fingerprint { get; private set; }

    [ObservableProperty]
    public partial string? Comment { get; private set; }

    [ObservableProperty]
    public partial string? PublicKeyText { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty]
    public partial bool IsEncrypted { get; private set; }

    [ObservableProperty]
    public partial bool StorePassphrase { get; set; }

    public bool CanCopyPublicKey => !string.IsNullOrWhiteSpace(PublicKeyText);

    public async Task InspectAsync(
        ReadOnlyMemory<char> passphrase = default,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SelectedPath))
        {
            ClearInspection();
            return;
        }

        try
        {
            var inspection = await _keyService.InspectAsync(SelectedPath, passphrase, cancellationToken).ConfigureAwait(true);
            SelectedPath = inspection.Path;
            KeyType = inspection.KeyType;
            Sha256Fingerprint = inspection.Sha256Fingerprint;
            Comment = inspection.Comment;
            PublicKeyText = inspection.PublicKeyText;
            IsEncrypted = inspection.IsEncrypted;
            StatusMessage = inspection.IsEncrypted && inspection.Sha256Fingerprint is null
                ? "This key is encrypted. Enter its passphrase to verify the type and fingerprint; you may store that passphrase with the connection."
                : $"{inspection.Format} key verified. Review its identity before saving the connection.";
            AddRecent(inspection.Path);
            OnPropertyChanged(nameof(CanCopyPublicKey));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ClearInspection();
            StatusMessage = exception.Message;
        }
    }

    public async Task GenerateAsync(string path, CancellationToken cancellationToken = default)
    {
        var inspection = await _keyService.GenerateEd25519Async(
            path,
            $"RemoteFlow {Environment.UserName}",
            cancellationToken).ConfigureAwait(true);
        SelectedPath = inspection.Path;
        await InspectAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    public async Task CopyPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(PublicKeyText))
        {
            return;
        }
        var result = await _clipboard.WriteTextAsync(PublicKeyText, cancellationToken).ConfigureAwait(true);
        StatusMessage = result.Succeeded ? "Public key copied to the clipboard." : result.ErrorMessage;
    }

    private void AddRecent(string path)
    {
        var existing = RecentKeys.FirstOrDefault(item => string.Equals(item, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _ = RecentKeys.Remove(existing);
        }
        RecentKeys.Insert(0, path);
        while (RecentKeys.Count > 8)
        {
            RecentKeys.RemoveAt(RecentKeys.Count - 1);
        }
    }

    private void ClearInspection()
    {
        KeyType = null;
        Sha256Fingerprint = null;
        Comment = null;
        PublicKeyText = null;
        IsEncrypted = false;
        OnPropertyChanged(nameof(CanCopyPublicKey));
    }
}
