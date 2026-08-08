using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Validation;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.UI.ViewModels.Connections;

public sealed record FolderChoiceViewModel(Guid? Id, string Path);

public sealed partial class TagChoiceViewModel(Guid id, string name) : ObservableObject
{
    public event EventHandler? SelectionChanged;

    public Guid Id { get; } = id;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed partial class ConnectionEditorViewModel : ObservableObject
{
    private readonly IConnectionService _connections;
    private readonly IConnectionRepository _connectionRepository;
    private readonly IConnectionCredentialService _credentials;
    private readonly IFolderRepository _folders;
    private readonly ITagRepository _tags;
    private readonly ITagService _tagService;
    private readonly ISettingsStore? _settings;
    private bool _loading;

    public ConnectionEditorViewModel(
        IConnectionService connections,
        IConnectionRepository connectionRepository,
        IConnectionCredentialService credentials,
        IFolderRepository folders,
        ITagRepository tags,
        ITagService tagService,
        ISshKeyService? sshKeyService = null,
        IClipboardService? clipboard = null,
        ISettingsStore? settings = null)
    {
        _connections = connections;
        _connectionRepository = connectionRepository;
        _credentials = credentials;
        _folders = folders;
        _tags = tags;
        _tagService = tagService;
        _settings = settings;
        if (sshKeyService is not null && clipboard is not null)
        {
            KeyPicker = new SshKeyPickerViewModel(sshKeyService, clipboard);
            KeyPicker.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(SshKeyPickerViewModel.SelectedPath))
                {
                    PrivateKeyPath = KeyPicker.SelectedPath;
                }
            };
        }
        Protocols = Enum.GetValues<ProtocolType>();
        AuthMethods = Enum.GetValues<AuthMethod>();
        Environments = Enum.GetValues<EnvironmentKind>();
        HostKeyPolicies = Enum.GetValues<HostKeyPolicy>();
        UpdateConditionalProperties();
        UpdateEnvironmentPreview();
    }

    public Guid? ConnectionId { get; private set; }

    public bool IsNew => ConnectionId is null;

    public string Title => IsNew ? "New connection" : $"Edit {Name}";

    public IReadOnlyList<ProtocolType> Protocols { get; }

    public IReadOnlyList<AuthMethod> AuthMethods { get; }

    public IReadOnlyList<EnvironmentKind> Environments { get; }

    public IReadOnlyList<HostKeyPolicy> HostKeyPolicies { get; }

    public ObservableCollection<FolderChoiceViewModel> FolderChoices { get; } = [];

    public ObservableCollection<TagChoiceViewModel> TagChoices { get; } = [];

    public SshKeyPickerViewModel? KeyPicker { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Host { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int Port { get; set; } = ProtocolType.Ssh.GetDefaultPort();

    [ObservableProperty]
    public partial ProtocolType Protocol { get; set; } = ProtocolType.Ssh;

    [ObservableProperty]
    public partial string? Username { get; set; }

    [ObservableProperty]
    public partial AuthMethod AuthMethod { get; set; }

    [ObservableProperty]
    public partial string? Notes { get; set; }

    [ObservableProperty]
    public partial FolderChoiceViewModel? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial EnvironmentKind Environment { get; set; }

    [ObservableProperty]
    public partial string? ColorOverrideHex { get; set; }

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    [ObservableProperty]
    public partial string? PrivateKeyPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostKeyPolicyHint))]
    public partial HostKeyPolicy HostKeyPolicy { get; set; } = HostKeyPolicy.TrustOnFirstUse;

    public string HostKeyPolicyHint => HostKeyPolicy switch
    {
        HostKeyPolicy.Strict =>
            "Only connects when the host key is already trusted. It never prompts, so import the key from a known_hosts file first.",
        HostKeyPolicy.TrustOnFirstUse =>
            "Asks you to confirm the host key the first time, then requires it to match on every later connection.",
        HostKeyPolicy.AcceptAny =>
            "Accepts any host key without checking. Sessions are flagged as unverified and remain open to interception.",
        _ => throw new ArgumentOutOfRangeException(nameof(HostKeyPolicy)),
    };

    [ObservableProperty]
    public partial string? TagInput { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    [ObservableProperty]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    public partial bool IsSshSectionVisible { get; private set; }

    [ObservableProperty]
    public partial bool IsSftpSectionVisible { get; private set; }

    [ObservableProperty]
    public partial bool IsRdpSectionVisible { get; private set; }

    [ObservableProperty]
    public partial bool IsPrivateKeyPassphraseVisible { get; private set; }

    /// <summary>Whether the key picker applies — an SSH-family protocol authenticating with a key.</summary>
    [ObservableProperty]
    public partial bool IsPrivateKeySectionVisible { get; private set; }

    [ObservableProperty]
    public partial CredentialStorageStatus CredentialStatus { get; private set; }

    [ObservableProperty]
    public partial string CredentialStatusText { get; private set; } = "not stored";

    [ObservableProperty]
    public partial string? CredentialProviderName { get; private set; }

    [ObservableProperty]
    public partial IBrush EnvironmentPreviewBrush { get; private set; } = new SolidColorBrush(Color.Parse("#6CB6FF"));

    [ObservableProperty]
    public partial string? NameError { get; private set; }

    [ObservableProperty]
    public partial string? HostError { get; private set; }

    [ObservableProperty]
    public partial string? PortError { get; private set; }

    [ObservableProperty]
    public partial string? UsernameError { get; private set; }

    [ObservableProperty]
    public partial string? AuthMethodError { get; private set; }

    [ObservableProperty]
    public partial string? EnvironmentError { get; private set; }

    [ObservableProperty]
    public partial string? PrivateKeyPathError { get; private set; }

    [ObservableProperty]
    public partial string? SaveError { get; private set; }

    public bool CanClearCredential => CredentialStatus == CredentialStorageStatus.Stored;

    public string CredentialActionLabel => CredentialStatus switch
    {
        CredentialStorageStatus.Stored => "Rotate credential",
        CredentialStorageStatus.UnavailableOnThisMachine => "Re-enter credential",
        CredentialStorageStatus.NotStored => "Store credential",
        _ => throw new ArgumentOutOfRangeException(nameof(CredentialStatus)),
    };

    public async Task InitializeAsync(Guid? connectionId, CancellationToken cancellationToken = default)
    {
        _loading = true;
        try
        {
            ConnectionId = connectionId;
            FolderChoices.Clear();
            FolderChoices.Add(new FolderChoiceViewModel(null, "No folder"));
            foreach (var folder in (await _folders.ListAsync(cancellationToken).ConfigureAwait(true))
                         .OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase))
            {
                FolderChoices.Add(new FolderChoiceViewModel(folder.Id, folder.Path));
            }

            TagChoices.Clear();
            foreach (var tag in (await _tags.ListAsync(cancellationToken).ConfigureAwait(true))
                         .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase))
            {
                var choice = new TagChoiceViewModel(tag.Id, tag.Name);
                choice.SelectionChanged += OnTagSelectionChanged;
                TagChoices.Add(choice);
            }

            if (connectionId is { } id)
            {
                var connection = await _connectionRepository.GetByIdAsync(id, cancellationToken).ConfigureAwait(true)
                    ?? throw new KeyNotFoundException($"Connection '{id}' was not found.");
                LoadConnection(connection);
                await RefreshCredentialStateAsync(cancellationToken).ConfigureAwait(true);
            }
            else
            {
                SelectedFolder = FolderChoices[0];
                CredentialStatus = CredentialStorageStatus.NotStored;
                UpdateCredentialPresentation();
                if (_settings is not null)
                {
                    HostKeyPolicy = await _settings
                        .Get(SettingKeys.DefaultHostKeyPolicy, cancellationToken)
                        .ConfigureAwait(true);
                }
            }

            if (KeyPicker is not null)
            {
                await KeyPicker.RefreshAvailableKeysAsync(cancellationToken).ConfigureAwait(true);
            }

            ClearValidationErrors();
            IsDirty = false;
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(Title));
        }
        finally
        {
            _loading = false;
        }
    }

    public async Task<bool> SaveAsync(
        ReadOnlyMemory<char> capturedSecret,
        CancellationToken cancellationToken = default)
    {
        if (!Validate())
        {
            return false;
        }

        IsSaving = true;
        SaveError = null;
        try
        {
            var input = new ConnectionInput(
                Name,
                Host,
                Port,
                Protocol,
                Username,
                AuthMethod,
                Notes,
                SelectedFolder?.Id,
                Environment,
                ColorOverrideHex,
                PrivateKeyPath,
                HostKeyPolicy);
            var saved = ConnectionId is { } id
                ? await _connections.UpdateAsync(id, input, cancellationToken).ConfigureAwait(true)
                : await _connections.CreateAsync(input, cancellationToken).ConfigureAwait(true);
            if (saved.IsFailure)
            {
                ApplySaveError(saved.Error);
                return false;
            }

            ConnectionId = saved.Value.Id;
            var connection = saved.Value;
            if (connection.IsFavorite != IsFavorite)
            {
                var favorite = await _connections.ToggleFavoriteAsync(connection.Id, cancellationToken).ConfigureAwait(true);
                if (favorite.IsFailure)
                {
                    ApplySaveError(favorite.Error);
                    return false;
                }

                connection = favorite.Value;
            }

            if (!await MaterializeTagInputAsync(cancellationToken).ConfigureAwait(true) ||
                !await SynchronizeTagsAsync(connection, cancellationToken).ConfigureAwait(true))
            {
                return false;
            }

            if (!capturedSecret.IsEmpty)
            {
                var kind = GetCredentialKind();
                var stored = await _credentials.StoreAsync(
                    connection.Id,
                    kind,
                    capturedSecret,
                    connection.Name,
                    cancellationToken).ConfigureAwait(true);
                if (stored.IsFailure)
                {
                    ApplySaveError(stored.Error);
                    return false;
                }
            }
            else if (AuthMethod == AuthMethod.None && !connection.Credential.IsEmpty)
            {
                var cleared = await _credentials.ClearAsync(connection.Id, cancellationToken).ConfigureAwait(true);
                if (cleared.IsFailure)
                {
                    ApplySaveError(cleared.Error);
                    return false;
                }
            }

            await RefreshCredentialStateAsync(cancellationToken).ConfigureAwait(true);
            IsDirty = false;
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(Title));
            return true;
        }
        finally
        {
            if (!capturedSecret.IsEmpty && MemoryMarshal.TryGetArray(capturedSecret, out var segment) && segment.Array is not null)
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(segment.Array.AsSpan()));
            }

            IsSaving = false;
        }
    }

    public async Task<bool> ClearCredentialAsync(CancellationToken cancellationToken = default)
    {
        if (ConnectionId is not { } id)
        {
            return true;
        }

        var result = await _credentials.ClearAsync(id, cancellationToken).ConfigureAwait(true);
        if (result.IsFailure)
        {
            ApplySaveError(result.Error);
            return false;
        }

        await RefreshCredentialStateAsync(cancellationToken).ConfigureAwait(true);
        return true;
    }

    private void LoadConnection(Connection connection)
    {
        Name = connection.Name;
        Host = connection.Host;
        Port = connection.Port;
        Protocol = connection.Protocol;
        Username = connection.Username;
        AuthMethod = connection.AuthMethod;
        Notes = connection.Notes;
        SelectedFolder = FolderChoices.First(choice => choice.Id == connection.FolderId);
        Environment = connection.Environment;
        ColorOverrideHex = connection.ColorOverrideHex;
        IsFavorite = connection.IsFavorite;
        PrivateKeyPath = connection.Ssh.PrivateKeyPath;
        HostKeyPolicy = connection.Ssh.HostKeyPolicy;
        var selectedTagIds = connection.Tags.Select(tag => tag.TagId).ToHashSet();
        foreach (var choice in TagChoices)
        {
            choice.IsSelected = selectedTagIds.Contains(choice.Id);
        }

        UpdateConditionalProperties();
        UpdateEnvironmentPreview();
    }

    private async Task<bool> SynchronizeTagsAsync(Connection connection, CancellationToken cancellationToken)
    {
        var current = connection.Tags.Select(tag => tag.TagId).ToHashSet();
        var selected = TagChoices.Where(choice => choice.IsSelected).Select(choice => choice.Id).ToHashSet();
        foreach (var tagId in selected.Except(current))
        {
            var result = await _tagService.AssignAsync(connection.Id, tagId, cancellationToken).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ApplySaveError(result.Error);
                return false;
            }
        }

        foreach (var tagId in current.Except(selected))
        {
            var result = await _tagService.UnassignAsync(connection.Id, tagId, cancellationToken).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ApplySaveError(result.Error);
                return false;
            }
        }

        return true;
    }

    private async Task<bool> MaterializeTagInputAsync(CancellationToken cancellationToken)
    {
        var names = (TagInput ?? string.Empty)
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var name in names)
        {
            var created = await _tagService.CreateAsync(name, cancellationToken: cancellationToken).ConfigureAwait(true);
            if (created.IsFailure)
            {
                ApplySaveError(created.Error);
                return false;
            }

            var choice = TagChoices.FirstOrDefault(candidate => candidate.Id == created.Value.Id);
            if (choice is null)
            {
                choice = new TagChoiceViewModel(created.Value.Id, created.Value.Name);
                choice.SelectionChanged += OnTagSelectionChanged;
                TagChoices.Add(choice);
            }

            choice.IsSelected = true;
        }

        TagInput = null;
        return true;
    }

    private async Task RefreshCredentialStateAsync(CancellationToken cancellationToken)
    {
        if (ConnectionId is not { } id)
        {
            return;
        }

        var info = await _credentials.InspectAsync(id, cancellationToken).ConfigureAwait(true);
        CredentialStatus = info.Status;
        CredentialProviderName = info.ProviderName;
        UpdateCredentialPresentation();
    }

    private bool Validate()
    {
        ClearValidationErrors();
        var errors = ConnectionValidator.Validate(new ConnectionInput(
            Name,
            Host,
            Port,
            Protocol,
            Username,
            AuthMethod,
            Notes,
            SelectedFolder?.Id,
            Environment,
            ColorOverrideHex,
            PrivateKeyPath));
        foreach (var error in errors)
        {
            ApplyValidationError(error);
        }

        return errors.Count == 0;
    }

    private void ApplyValidationError(RemoteFlowError error)
    {
        switch (error.Code)
        {
            case "connection.name": NameError = error.Message; break;
            case "connection.host": HostError = error.Message; break;
            case "connection.port": PortError = error.Message; break;
            case "connection.username": UsernameError = error.Message; break;
            case "connection.auth_method": AuthMethodError = error.Message; break;
            case "connection.environment": EnvironmentError = error.Message; break;
            case "connection.private_key_path": PrivateKeyPathError = error.Message; break;
            default: SaveError = error.Message; break;
        }
    }

    private void ApplySaveError(RemoteFlowError error)
    {
        ApplyValidationError(error);
        SaveError ??= error.Message;
    }

    private void ClearValidationErrors()
    {
        NameError = null;
        HostError = null;
        PortError = null;
        UsernameError = null;
        AuthMethodError = null;
        EnvironmentError = null;
        PrivateKeyPathError = null;
        SaveError = null;
    }

    private CredentialKind GetCredentialKind()
    {
        return AuthMethod == AuthMethod.PrivateKey
            ? CredentialKind.PrivateKeyPassphrase
            : Protocol == ProtocolType.Rdp ? CredentialKind.RdpPassword : CredentialKind.Password;
    }

    private void MarkDirty()
    {
        if (!_loading)
        {
            IsDirty = true;
            OnPropertyChanged(nameof(Title));
        }
    }

    private void OnTagSelectionChanged(object? sender, EventArgs e)
    {
        MarkDirty();
    }

    private void UpdateConditionalProperties()
    {
        IsSshSectionVisible = Protocol is ProtocolType.Ssh or ProtocolType.Sftp;
        IsSftpSectionVisible = Protocol == ProtocolType.Sftp;
        IsRdpSectionVisible = Protocol == ProtocolType.Rdp;
        IsPrivateKeyPassphraseVisible = AuthMethod == AuthMethod.PrivateKey;
        IsPrivateKeySectionVisible = IsSshSectionVisible && AuthMethod == AuthMethod.PrivateKey;
    }

    private void UpdateEnvironmentPreview()
    {
        var fallback = Environment switch
        {
            EnvironmentKind.Unspecified => "#6CB6FF",
            EnvironmentKind.Development => "#5DE28C",
            EnvironmentKind.Staging => "#FFCA58",
            EnvironmentKind.Production => "#FF7B72",
            _ => throw new ArgumentOutOfRangeException(nameof(Environment)),
        };
        EnvironmentPreviewBrush = new SolidColorBrush(
            Color.TryParse(ColorOverrideHex, out var color) ? color : Color.Parse(fallback));
    }

    private void UpdateCredentialPresentation()
    {
        CredentialStatusText = CredentialStatus switch
        {
            CredentialStorageStatus.Stored => "stored",
            CredentialStorageStatus.UnavailableOnThisMachine => "unavailable on this machine",
            CredentialStorageStatus.NotStored => "not stored",
            _ => throw new ArgumentOutOfRangeException(nameof(CredentialStatus)),
        };
        OnPropertyChanged(nameof(CanClearCredential));
        OnPropertyChanged(nameof(CredentialActionLabel));
    }

    partial void OnNameChanged(string value) { MarkDirty(); }
    partial void OnHostChanged(string value) { MarkDirty(); }
    partial void OnPortChanged(int value) { MarkDirty(); }
    partial void OnUsernameChanged(string? value) { MarkDirty(); }
    partial void OnNotesChanged(string? value) { MarkDirty(); }
    partial void OnSelectedFolderChanged(FolderChoiceViewModel? value) { MarkDirty(); }
    partial void OnIsFavoriteChanged(bool value) { MarkDirty(); }
    partial void OnPrivateKeyPathChanged(string? value)
    {
        if (KeyPicker is not null && !string.Equals(KeyPicker.SelectedPath, value, StringComparison.Ordinal))
        {
            KeyPicker.SelectedPath = value;
        }
        MarkDirty();
    }
    partial void OnTagInputChanged(string? value) { MarkDirty(); }
    partial void OnHostKeyPolicyChanged(HostKeyPolicy value) { MarkDirty(); }

    partial void OnProtocolChanged(ProtocolType oldValue, ProtocolType newValue)
    {
        if (!_loading && Port == oldValue.GetDefaultPort())
        {
            Port = newValue.GetDefaultPort();
        }

        UpdateConditionalProperties();
        MarkDirty();
    }

    partial void OnAuthMethodChanged(AuthMethod value)
    {
        UpdateConditionalProperties();
        MarkDirty();
    }

    partial void OnEnvironmentChanged(EnvironmentKind value)
    {
        UpdateEnvironmentPreview();
        MarkDirty();
    }

    partial void OnColorOverrideHexChanged(string? value)
    {
        UpdateEnvironmentPreview();
        MarkDirty();
    }
}
