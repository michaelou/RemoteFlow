using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.UI.ViewModels.Connections;

/// <summary>One selectable private key, either discovered in <c>~/.ssh</c> or chosen by the user.</summary>
public sealed record SshKeyOption(string Path, string DisplayName, string Detail)
{
    public static SshKeyOption FromInspection(SshKeyInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        return new(
            inspection.Path,
            System.IO.Path.GetFileName(inspection.Path),
            Describe(inspection));
    }

    public static SshKeyOption FromPath(string path)
    {
        return new(path, System.IO.Path.GetFileName(path), path);
    }

    private static string Describe(SshKeyInspection inspection)
    {
        if (inspection.IsEncrypted && inspection.Sha256Fingerprint is null)
        {
            return "encrypted — passphrase required";
        }

        var parts = new[] { inspection.KeyType, inspection.Comment }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        return string.Join(" · ", parts);
    }
}

public sealed partial class SshKeyPickerViewModel(
    ISshKeyService keyService,
    IClipboardService clipboard) : ObservableObject
{
    private readonly ISshKeyService _keyService = keyService ?? throw new ArgumentNullException(nameof(keyService));
    private readonly IClipboardService _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    private bool _syncingSelection;

    /// <summary>Keys found in <see cref="ISshKeyService.DefaultKeyDirectory" />, plus the current selection.</summary>
    public ObservableCollection<SshKeyOption> AvailableKeys { get; } = [];

    public string DefaultKeyDirectory => _keyService.DefaultKeyDirectory;

    public string ImportLocationHint => $"The key is saved into {_keyService.DefaultKeyDirectory} and selected for this connection.";

    public string NoKeysHint =>
        $"No private keys found in {_keyService.DefaultKeyDirectory}. Browse for one, paste a key, or generate a new Ed25519 key.";

    [ObservableProperty]
    public partial SshKeyOption? SelectedKey { get; set; }

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

    [ObservableProperty]
    public partial bool IsImportOpen { get; private set; }

    [ObservableProperty]
    public partial string ImportFileName { get; set; } = "id_ed25519";

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public bool CanCopyPublicKey => !string.IsNullOrWhiteSpace(PublicKeyText);

    public bool HasKeyDetails => !string.IsNullOrWhiteSpace(Sha256Fingerprint);

    public bool HasNoDiscoveredKeys => AvailableKeys.Count == 0;

    /// <summary>Populates <see cref="AvailableKeys" /> from <c>~/.ssh</c>.</summary>
    public async Task RefreshAvailableKeysAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SshKeyInspection> discovered;
        IsBusy = true;
        try
        {
            discovered = await _keyService.DiscoverAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            discovered = [];
            StatusMessage = $"{_keyService.DefaultKeyDirectory} could not be read: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }

        var current = SelectedPath;
        AvailableKeys.Clear();
        foreach (var key in discovered)
        {
            AvailableKeys.Add(SshKeyOption.FromInspection(key));
        }

        if (!string.IsNullOrWhiteSpace(current) &&
            !AvailableKeys.Any(option => PathsMatch(option.Path, current)))
        {
            AvailableKeys.Insert(0, SshKeyOption.FromPath(current));
        }

        OnPropertyChanged(nameof(HasNoDiscoveredKeys));
        SyncSelectedKeyToPath();
    }

    public async Task InspectAsync(
        ReadOnlyMemory<char> passphrase = default,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(SelectedPath))
        {
            ClearInspection();
            StatusMessage = null;
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
            AddOption(SshKeyOption.FromInspection(inspection));
            NotifyInspectionChanged();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ClearInspection();
            StatusMessage = exception.Message;
        }
    }

    public async Task GenerateAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var inspection = await _keyService.GenerateEd25519Async(
                path,
                $"RemoteFlow {Environment.UserName}",
                cancellationToken).ConfigureAwait(true);
            SelectedPath = inspection.Path;
            await InspectAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"The key could not be generated: {exception.Message}";
        }
    }

    /// <summary>Writes pasted key text into <c>~/.ssh</c> and selects it.</summary>
    /// <remarks>
    /// The text is passed in from the view rather than bound to a property so that private-key
    /// material is never held on the view model, matching how the editor captures passwords.
    /// </remarks>
    public async Task ImportAsync(string? privateKeyText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(privateKeyText))
        {
            StatusMessage = "Paste the private key text first.";
            return;
        }

        var name = ImportFileName?.Trim();
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusMessage = "Enter a file name for the key, without a path.";
            return;
        }

        try
        {
            var inspection = await _keyService.ImportAsync(
                Path.Combine(_keyService.DefaultKeyDirectory, name),
                privateKeyText,
                cancellationToken).ConfigureAwait(true);
            IsImportOpen = false;
            SelectedPath = inspection.Path;
            await InspectAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusMessage = exception.Message;
        }
    }

    public void OpenImport()
    {
        IsImportOpen = true;
        StatusMessage = null;
    }

    public void CancelImport()
    {
        IsImportOpen = false;
    }

    public async Task CopyPublicKeyAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(PublicKeyText))
        {
            return;
        }
        var result = await _clipboard.WriteTextAsync(PublicKeyText, cancellationToken).ConfigureAwait(true);
        StatusMessage = result.Succeeded
            ? "Public key copied. Add it to ~/.ssh/authorized_keys on the server."
            : result.ErrorMessage;
    }

    partial void OnSelectedKeyChanged(SshKeyOption? value)
    {
        if (_syncingSelection || value is null || PathsMatch(value.Path, SelectedPath))
        {
            return;
        }

        SelectedPath = value.Path;
        _ = InspectAsync();
    }

    partial void OnSelectedPathChanged(string? value)
    {
        SyncSelectedKeyToPath();
    }

    partial void OnPublicKeyTextChanged(string? value)
    {
        OnPropertyChanged(nameof(CanCopyPublicKey));
    }

    partial void OnSha256FingerprintChanged(string? value)
    {
        OnPropertyChanged(nameof(HasKeyDetails));
    }

    private void SyncSelectedKeyToPath()
    {
        _syncingSelection = true;
        try
        {
            SelectedKey = AvailableKeys.FirstOrDefault(option => PathsMatch(option.Path, SelectedPath));
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void AddOption(SshKeyOption option)
    {
        var existing = AvailableKeys.FirstOrDefault(item => PathsMatch(item.Path, option.Path));
        if (existing is not null)
        {
            AvailableKeys[AvailableKeys.IndexOf(existing)] = option;
        }
        else
        {
            AvailableKeys.Insert(0, option);
            OnPropertyChanged(nameof(HasNoDiscoveredKeys));
        }

        SyncSelectedKeyToPath();
    }

    private static bool PathsMatch(string? left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearInspection()
    {
        KeyType = null;
        Sha256Fingerprint = null;
        Comment = null;
        PublicKeyText = null;
        IsEncrypted = false;
        NotifyInspectionChanged();
    }

    private void NotifyInspectionChanged()
    {
        OnPropertyChanged(nameof(CanCopyPublicKey));
        OnPropertyChanged(nameof(HasKeyDetails));
    }
}
