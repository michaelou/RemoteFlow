using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Application.Services;
using RemoteFlow.Application.Validation;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.UI.ViewModels.Connections;

public sealed record FolderChoiceViewModel(Guid? Id, string Path);

/// <summary>One entry in the environment colour override picker. <paramref name="Hex"/> is null for the
/// "inherit the environment colour" entry and for the custom entry, which takes its value from the hex box.</summary>
public sealed record ColorOverrideChoiceViewModel(string Label, string? Hex, bool IsCustom = false)
{
    public IBrush Swatch { get; } = Hex is null
        ? Brushes.Transparent
        : new SolidColorBrush(Color.Parse(Hex));

    public bool HasSwatch => Hex is not null;
}

/// <summary>One entry in the RDP resolution picker. The "Fit to client" entry carries no size, and the
/// custom entry takes its size from the width and height boxes.</summary>
public sealed record RdpResolutionChoiceViewModel(string Label, int? Width, int? Height, bool IsCustom = false);

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
    private readonly IRdpLauncher? _rdpLauncher;
    private bool _loading;
    private bool _syncingColorChoice;
    private bool _syncingResolutionChoice;
    private string? _derivedHost;

    public ConnectionEditorViewModel(
        IConnectionService connections,
        IConnectionRepository connectionRepository,
        IConnectionCredentialService credentials,
        IFolderRepository folders,
        ITagRepository tags,
        ITagService tagService,
        ISshKeyService? sshKeyService = null,
        IClipboardService? clipboard = null,
        ISettingsStore? settings = null,
        IRdpLauncher? rdpLauncher = null)
    {
        _connections = connections;
        _connectionRepository = connectionRepository;
        _credentials = credentials;
        _folders = folders;
        _tags = tags;
        _tagService = tagService;
        _settings = settings;
        _rdpLauncher = rdpLauncher;
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
        SelectedColorOverride = ColorOverrideChoices[0];
        SelectedRdpResolution = RdpResolutionChoices[0];
        RdpInstallGuidance = _rdpLauncher?.MissingClientGuidance
            ?? "No RDP launcher is available in this build.";
        UpdateConditionalProperties();
        UpdateEnvironmentPreview();
    }

    /// <summary>Accent colours picked to stay legible against the dark surfaces, and far enough apart in
    /// hue from each other and from the environment defaults to tell two sessions apart at a glance.</summary>
    public static IReadOnlyList<ColorOverrideChoiceViewModel> ColorOverrideChoices { get; } =
    [
        new("Match environment", null),
        new("Teal", "#4FD1C5"),
        new("Violet", "#A78BFA"),
        new("Amber", "#F5B14C"),
        new("Rose", "#FF7EB6"),
        new("Custom…", null, IsCustom: true),
    ];

    /// <summary>The sizes worth one click. Anything else goes through "Custom…" and the two boxes.</summary>
    public static IReadOnlyList<RdpResolutionChoiceViewModel> RdpResolutionChoices { get; } =
    [
        new("Fit to the client window", null, null),
        new("1280 × 720", 1_280, 720),
        new("1366 × 768", 1_366, 768),
        new("1600 × 900", 1_600, 900),
        new("1920 × 1080", 1_920, 1_080),
        new("2560 × 1440", 2_560, 1_440),
        new("3840 × 2160", 3_840, 2_160),
        new("Custom…", null, null, IsCustom: true),
    ];

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
    [NotifyPropertyChangedFor(nameof(UsernameLabel))]
    [NotifyPropertyChangedFor(nameof(CredentialCaptureLabel))]
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
    public partial ColorOverrideChoiceViewModel SelectedColorOverride { get; set; }

    [ObservableProperty]
    public partial bool IsCustomColorVisible { get; private set; }

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

    /// <summary>The identifier box means something different per protocol, and calling an access key ID a
    /// "username" is the kind of small lie that makes a form hard to fill in.</summary>
    public string UsernameLabel => Protocol switch
    {
        ProtocolType.S3 => "Access key ID",
        ProtocolType.AzureBlob => "Storage account name",
        ProtocolType.Ssh or ProtocolType.Sftp or ProtocolType.Rdp => "Username",
        _ => throw new ArgumentOutOfRangeException(nameof(Protocol)),
    };

    public string CredentialCaptureLabel => Protocol switch
    {
        ProtocolType.S3 => "Secret access key",
        ProtocolType.AzureBlob => "Account key",
        ProtocolType.Ssh or ProtocolType.Sftp or ProtocolType.Rdp => "Password",
        _ => throw new ArgumentOutOfRangeException(nameof(Protocol)),
    };

    [ObservableProperty]
    public partial string? RdpDomain { get; set; }

    [ObservableProperty]
    public partial bool RdpFullScreen { get; set; }

    [ObservableProperty]
    public partial bool RdpMultimon { get; set; }

    [ObservableProperty]
    public partial bool RdpRedirectClipboard { get; set; } = true;

    [ObservableProperty]
    public partial bool RdpRedirectDrives { get; set; }

    [ObservableProperty]
    public partial RdpResolutionChoiceViewModel SelectedRdpResolution { get; set; }

    [ObservableProperty]
    public partial bool IsCustomRdpResolutionVisible { get; private set; }

    [ObservableProperty]
    public partial string? RdpWidthText { get; set; }

    [ObservableProperty]
    public partial string? RdpHeightText { get; set; }

    [ObservableProperty]
    public partial string? RdpResolutionError { get; private set; }

    /// <summary>What the RDP section says about this machine's client, refreshed each time the section
    /// appears.</summary>
    [ObservableProperty]
    public partial string RdpClientStatusText { get; private set; } = "Looking for an RDP client…";

    [ObservableProperty]
    public partial bool IsRdpClientMissing { get; private set; }

    public string RdpInstallGuidance { get; }

    /// <summary>Completes when the client detection kicked off by showing the section has finished.</summary>
    public Task RdpClientDetectionSettled { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial string? StorageRegion { get; set; }

    [ObservableProperty]
    public partial string? StorageServiceUrl { get; set; }

    [ObservableProperty]
    public partial bool StorageUsePathStyleAddressing { get; set; }

    [ObservableProperty]
    public partial string? StorageContainer { get; set; }

    [ObservableProperty]
    public partial string? StorageRootPrefix { get; set; }

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
    public partial bool IsStorageSectionVisible { get; private set; }

    /// <summary>Whether the protocol-options panel holds anything. Three protocols bring extra fields and
    /// the other two bring none, so without this the panel would show as an empty card with a heading.</summary>
    public bool IsProtocolOptionsVisible => IsSftpSectionVisible || IsRdpSectionVisible || IsStorageSectionVisible;

    /// <summary>Only S3 carries a region; Azure's is implied by the account.</summary>
    [ObservableProperty]
    public partial bool IsStorageRegionVisible { get; private set; }

    /// <summary>A custom endpoint and path-style addressing only mean anything for S3-compatible
    /// services. Azure reaches a sovereign cloud through the host box instead.</summary>
    [ObservableProperty]
    public partial bool IsStorageEndpointVisible { get; private set; }

    /// <summary>Hidden for object storage: an access key is not one of the SSH authentication methods,
    /// and the combo is <c>Enum.GetValues&lt;AuthMethod&gt;()</c>, so leaving it visible would offer
    /// choices that mean nothing here.</summary>
    [ObservableProperty]
    public partial bool IsAuthMethodVisible { get; private set; } = true;

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
    public partial string? ColorOverrideError { get; private set; }

    [ObservableProperty]
    public partial string? PrivateKeyPathError { get; private set; }

    [ObservableProperty]
    public partial string? StorageRegionError { get; private set; }

    /// <summary>Said as a warning rather than as a validation error: the field is also used by
    /// S3-compatible services, where the region is whatever that deployment calls it, so an unknown value
    /// must not block a save. It exists because the alternative is finding out at connect time, from a DNS
    /// failure, that <c>eu-west</c> is not a region.</summary>
    [ObservableProperty]
    public partial string? StorageRegionWarning { get; private set; }

    /// <summary>Suggestions, not a closed set — see <see cref="S3Regions"/>.</summary>
    public IReadOnlyList<S3Region> StorageRegionChoices { get; } = S3Regions.All;

    [ObservableProperty]
    public partial string? StorageServiceUrlError { get; private set; }

    [ObservableProperty]
    public partial string? StorageContainerError { get; private set; }

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
            _syncingColorChoice = true;
            SelectedColorOverride = ColorOverrideChoices[0];
            _syncingColorChoice = false;
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

            if (IsRdpSectionVisible)
            {
                await RefreshRdpClientsAsync(cancellationToken).ConfigureAwait(true);
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
            var input = BuildInput();
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
            // Object storage connections legitimately sit on AuthMethod.None, so without this guard
            // re-saving one would delete the secret key it had just been given.
            else if (AuthMethod == AuthMethod.None &&
                     !Protocol.IsObjectStorage() &&
                     !connection.Credential.IsEmpty)
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
        RdpDomain = connection.Rdp.Domain;
        RdpFullScreen = connection.Rdp.FullScreen;
        RdpMultimon = connection.Rdp.Multimon;
        RdpRedirectClipboard = connection.Rdp.RedirectClipboard;
        RdpRedirectDrives = connection.Rdp.RedirectDrives;
        LoadResolution(connection.Rdp.Width, connection.Rdp.Height);
        StorageRegion = connection.ObjectStorage.Region;
        StorageServiceUrl = connection.ObjectStorage.ServiceUrl;
        StorageUsePathStyleAddressing = connection.ObjectStorage.UsePathStyleAddressing;
        StorageContainer = connection.ObjectStorage.Container;
        StorageRootPrefix = connection.ObjectStorage.RootPrefix;
        // Seeding this from the saved values is what tells a hand-edited host from a derived one: if the
        // stored host is what we would have derived, later edits keep it in step; if it is not, it is the
        // user's and stays untouched.
        _derivedHost = ObjectStorageEndpoint.DeriveHost(
            connection.Protocol,
            connection.ObjectStorage.Region,
            connection.Username,
            connection.ObjectStorage.ServiceUrl);
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
        UpdateRdpResolutionError();
        var errors = ConnectionValidator.Validate(BuildInput());
        foreach (var error in errors)
        {
            ApplyValidationError(error);
        }

        return errors.Count == 0 && RdpResolutionError is null;
    }

    private ConnectionInput BuildInput()
    {
        var (width, height, _) = ReadResolution();
        return new ConnectionInput(
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
            HostKeyPolicy,
            RdpDomain,
            RdpFullScreen,
            width,
            height,
            RdpMultimon,
            RdpRedirectClipboard,
            RdpRedirectDrives,
            StorageRegion,
            StorageServiceUrl,
            StorageUsePathStyleAddressing,
            StorageContainer,
            StorageRootPrefix);
    }

    /// <summary>Reads the two boxes. Anything that is not a whole number of pixels comes back as a parse
    /// error rather than as a dimension, so it can be rejected where it was typed.</summary>
    private (int? Width, int? Height, string? ParseError) ReadResolution()
    {
        var width = ParseDimension(RdpWidthText, out var widthIsGarbage);
        var height = ParseDimension(RdpHeightText, out var heightIsGarbage);
        return (
            width,
            height,
            widthIsGarbage || heightIsGarbage
                ? "Enter the width and height as whole numbers of pixels."
                : null);
    }

    private void UpdateRdpResolutionError()
    {
        var (width, height, parseError) = ReadResolution();
        var errors = ConnectionValidator.ValidateRdpResolution(width, height);
        RdpResolutionError = parseError ?? (errors.Count == 0 ? null : errors[0].Message);
    }

    private static int? ParseDimension(string? text, out bool isGarbage)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            isGarbage = false;
            return null;
        }

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            isGarbage = false;
            return value;
        }

        isGarbage = true;
        return null;
    }

    public async Task RefreshRdpClientsAsync(CancellationToken cancellationToken = default)
    {
        if (_rdpLauncher is null)
        {
            RdpClientStatusText = "RDP launching is not available in this build.";
            IsRdpClientMissing = true;
            return;
        }

        try
        {
            var clients = await _rdpLauncher.DetectClientsAsync(cancellationToken).ConfigureAwait(true);
            IsRdpClientMissing = clients.Count == 0;
            RdpClientStatusText = IsRdpClientMissing
                ? "No RDP client found on this machine."
                : "Using " + string.Join(", ", clients.Select(client => client.Description)) + ".";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            IsRdpClientMissing = true;
            RdpClientStatusText = $"The RDP client could not be detected: {exception.Message}";
        }
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
            case "connection.color": ColorOverrideError = error.Message; break;
            case "connection.private_key_path": PrivateKeyPathError = error.Message; break;
            case "connection.rdp_resolution": RdpResolutionError = error.Message; break;
            case "rdp.dimensions": RdpResolutionError = error.Message; break;
            case "connection.storage_region":
            case "storage.region": StorageRegionError = error.Message; break;
            case "connection.storage_service_url":
            case "storage.service_url": StorageServiceUrlError = error.Message; break;
            case "connection.storage_container":
            case "storage.container": StorageContainerError = error.Message; break;
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
        ColorOverrideError = null;
        PrivateKeyPathError = null;
        RdpResolutionError = null;
        StorageRegionError = null;
        StorageServiceUrlError = null;
        StorageContainerError = null;
        SaveError = null;
    }

    private CredentialKind GetCredentialKind()
    {
        return Protocol.IsObjectStorage()
            ? CredentialKind.StorageSecretKey
            : AuthMethod == AuthMethod.PrivateKey
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
        var rdpWasVisible = IsRdpSectionVisible;
        IsSshSectionVisible = Protocol is ProtocolType.Ssh or ProtocolType.Sftp;
        IsSftpSectionVisible = Protocol == ProtocolType.Sftp;
        IsRdpSectionVisible = Protocol == ProtocolType.Rdp;
        IsStorageSectionVisible = Protocol.IsObjectStorage();
        OnPropertyChanged(nameof(IsProtocolOptionsVisible));
        IsStorageRegionVisible = Protocol == ProtocolType.S3;
        IsStorageEndpointVisible = Protocol == ProtocolType.S3;
        IsAuthMethodVisible = !Protocol.IsObjectStorage();
        IsPrivateKeyPassphraseVisible = AuthMethod == AuthMethod.PrivateKey;
        IsPrivateKeySectionVisible = IsSshSectionVisible && AuthMethod == AuthMethod.PrivateKey;

        // Here rather than at each call site: this runs from the constructor, from LoadConnection, and on
        // every protocol change, which is exactly when the warning can start or stop applying.
        UpdateStorageRegionWarning();

        // A client can be installed or removed while the editor is open, so the indicator is answered
        // afresh every time the section comes into view rather than once per editor.
        if (IsRdpSectionVisible && !rdpWasVisible && !_loading)
        {
            RdpClientDetectionSettled = RefreshRdpClientsAsync();
        }
    }

    /// <summary>Puts a stored size onto the picker, falling back to "Custom…" for a size that is not one
    /// of the presets.</summary>
    private void LoadResolution(int? width, int? height)
    {
        _syncingResolutionChoice = true;
        try
        {
            RdpWidthText = width?.ToString(CultureInfo.InvariantCulture);
            RdpHeightText = height?.ToString(CultureInfo.InvariantCulture);
            var match = RdpResolutionChoices.FirstOrDefault(choice =>
                            !choice.IsCustom && choice.Width == width && choice.Height == height)
                        ?? RdpResolutionChoices.Single(choice => choice.IsCustom);
            SelectedRdpResolution = match;
            IsCustomRdpResolutionVisible = match.IsCustom;
        }
        finally
        {
            _syncingResolutionChoice = false;
        }

        UpdateRdpResolutionError();
    }

    /// <summary>Fills the host box from the storage fields. Overwriting only while the box still holds
    /// what we last put there is the rule the port box already follows, and hand-editing the host is how a
    /// sovereign-cloud account — <c>*.blob.core.chinacloudapi.cn</c> — is reached without another field.
    /// </summary>
    private void DeriveStorageHost()
    {
        if (_loading || !Protocol.IsObjectStorage())
        {
            return;
        }

        var derived = ObjectStorageEndpoint.DeriveHost(Protocol, StorageRegion, Username, StorageServiceUrl);
        if (derived is null)
        {
            return;
        }

        if (Host.Length == 0 || string.Equals(Host, _derivedHost, StringComparison.OrdinalIgnoreCase))
        {
            Host = derived;
        }

        _derivedHost = derived;
    }

    /// <summary>Warns when an S3 connection carries a region AWS does not have and there is no custom
    /// endpoint to explain it, naming the near misses so the fix is one click rather than a search.
    /// </summary>
    private void UpdateStorageRegionWarning()
    {
        if (Protocol != ProtocolType.S3 ||
            !string.IsNullOrWhiteSpace(StorageServiceUrl) ||
            string.IsNullOrWhiteSpace(StorageRegion) ||
            S3Regions.IsKnown(StorageRegion))
        {
            StorageRegionWarning = null;
            return;
        }

        var near = S3Regions.Suggest(StorageRegion);
        StorageRegionWarning = near.Count == 0
            ? $"'{StorageRegion.Trim()}' is not an AWS region. Connecting will fail to resolve " +
                $"{Host}. Leave it only if this is an S3-compatible service with its own endpoint."
            : $"'{StorageRegion.Trim()}' is not an AWS region. Did you mean {string.Join(", ", near)}?";
    }

    /// <summary>The colour the swatch shows for an environment nobody has overridden. Named rather than
    /// inlined because it is one of three places that state the same palette — the design tokens and the
    /// session accent are the others — and a test holds all three to the same answer.</summary>
    internal static string EnvironmentFallbackHex(EnvironmentKind environment)
    {
        return environment switch
        {
            EnvironmentKind.Unspecified => "#6CB6FF",
            EnvironmentKind.Development => "#FF7B72",
            EnvironmentKind.Staging => "#FFCA58",
            EnvironmentKind.Production => "#5DE28C",
            _ => throw new ArgumentOutOfRangeException(nameof(environment)),
        };
    }

    private void UpdateEnvironmentPreview()
    {
        EnvironmentPreviewBrush = new SolidColorBrush(
            Color.TryParse(ColorOverrideHex, out var color)
                ? color
                : Color.Parse(EnvironmentFallbackHex(Environment)));
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
    partial void OnUsernameChanged(string? value)
    {
        // Azure addresses the storage account, so the host follows the account name.
        DeriveStorageHost();
        MarkDirty();
    }
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
    partial void OnStorageUsePathStyleAddressingChanged(bool value) { MarkDirty(); }
    partial void OnStorageContainerChanged(string? value) { MarkDirty(); }
    partial void OnStorageRootPrefixChanged(string? value) { MarkDirty(); }

    partial void OnStorageRegionChanged(string? value)
    {
        DeriveStorageHost();
        UpdateStorageRegionWarning();
        MarkDirty();
    }

    partial void OnStorageServiceUrlChanged(string? value)
    {
        DeriveStorageHost();
        UpdateStorageRegionWarning();
        MarkDirty();
    }
    partial void OnHostKeyPolicyChanged(HostKeyPolicy value) { MarkDirty(); }
    partial void OnRdpDomainChanged(string? value) { MarkDirty(); }
    partial void OnRdpFullScreenChanged(bool value) { MarkDirty(); }
    partial void OnRdpMultimonChanged(bool value) { MarkDirty(); }
    partial void OnRdpRedirectClipboardChanged(bool value) { MarkDirty(); }
    partial void OnRdpRedirectDrivesChanged(bool value) { MarkDirty(); }

    partial void OnRdpWidthTextChanged(string? value)
    {
        UpdateRdpResolutionError();
        MarkDirty();
    }

    partial void OnRdpHeightTextChanged(string? value)
    {
        UpdateRdpResolutionError();
        MarkDirty();
    }

    partial void OnSelectedRdpResolutionChanged(RdpResolutionChoiceViewModel value)
    {
        IsCustomRdpResolutionVisible = value.IsCustom;
        if (_syncingResolutionChoice || value.IsCustom)
        {
            return;
        }

        // A preset owns both boxes; "Fit to the client window" carries no size and so clears them.
        RdpWidthText = value.Width?.ToString(CultureInfo.InvariantCulture);
        RdpHeightText = value.Height?.ToString(CultureInfo.InvariantCulture);
    }

    partial void OnProtocolChanged(ProtocolType oldValue, ProtocolType newValue)
    {
        if (!_loading && Port == oldValue.GetDefaultPort())
        {
            Port = newValue.GetDefaultPort();
        }

        if (!_loading && newValue.IsObjectStorage() && !oldValue.IsObjectStorage())
        {
            // A host that was right for SSH cannot be right for a storage account, so the box goes back
            // under the deriving rule as the protocol changes. Anything typed after that is the user's.
            _derivedHost = Host;
        }

        UpdateConditionalProperties();
        DeriveStorageHost();
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
        SyncColorOverrideChoice(value);
        UpdateEnvironmentPreview();
        MarkDirty();
    }

    partial void OnSelectedColorOverrideChanged(ColorOverrideChoiceViewModel value)
    {
        IsCustomColorVisible = value.IsCustom;
        if (!_syncingColorChoice && !value.IsCustom)
        {
            ColorOverrideHex = value.Hex;
        }
    }

    /// <summary>Moves the picker onto whichever entry matches the stored hex. Once the user is on
    /// "Custom…" the selection stays there, so typing a value that happens to match a preset does not
    /// close the hex box out from under them.</summary>
    private void SyncColorOverrideChoice(string? hex)
    {
        if (_syncingColorChoice || SelectedColorOverride.IsCustom)
        {
            return;
        }

        var match = ColorOverrideChoices.FirstOrDefault(choice =>
                        !choice.IsCustom && string.Equals(choice.Hex, hex, StringComparison.OrdinalIgnoreCase))
                    ?? ColorOverrideChoices.Single(choice => choice.IsCustom);
        _syncingColorChoice = true;
        SelectedColorOverride = match;
        _syncingColorChoice = false;
    }
}
