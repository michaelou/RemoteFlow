using RemoteFlow.Domain.Common;

namespace RemoteFlow.Domain.ValueObjects;

/// <summary>Which part of an object-storage account a connection points at, for both S3 and Azure Blob. A
/// connection is exactly one protocol, so the fields the other provider does not use are simply null —
/// one owned value object rather than two, and one <c>OwnsOne</c> block rather than two.
///
/// Every string is nullable and the single bool is not, deliberately: an owned type whose every column is
/// NULL materialises as <c>null</c>, and the required navigation configured for it then throws on query.
/// The non-nullable bool keeps at least one column non-NULL and makes the migration purely additive.</summary>
public sealed class ObjectStorageOptions
{
    private ObjectStorageOptions()
    {
    }

    /// <summary>The S3 region. Null for Azure Blob, which carries its region in the account.</summary>
    public string? Region { get; private set; }

    /// <summary>A custom S3-compatible endpoint — MinIO, Ceph/RGW, Backblaze B2, Cloudflare R2, Wasabi.
    /// Null means real AWS, addressed from <see cref="Region"/>.</summary>
    public string? ServiceUrl { get; private set; }

    /// <summary>Whether to address buckets as <c>{endpoint}/{bucket}</c> rather than
    /// <c>{bucket}.{endpoint}</c>. Most S3-compatible services need this; AWS itself does not.</summary>
    public bool UsePathStyleAddressing { get; private set; }

    /// <summary>The single bucket or container this connection is scoped to, or null to browse the whole
    /// account. A key scoped to one bucket usually cannot list buckets, which is why this exists.</summary>
    public string? Container { get; private set; }

    /// <summary>A key prefix inside the container to treat as the root, without leading or trailing
    /// separators.</summary>
    public string? RootPrefix { get; private set; }

    public string? LocalDownloadPath { get; private set; }

    public static ObjectStorageOptions Default()
    {
        return new();
    }

    /// <summary>The loose rule, not the S3-Azure intersection: Azure forbids dots and S3 allows them, so
    /// intersecting them would reject a name the user's own provider accepts. The provider is
    /// authoritative; anything stricter comes back from it as an invalid-path failure.</summary>
    public static bool IsValidContainerName(string? value)
    {
        var name = value?.Trim();
        if (name is null || name.Length is < 3 or > 63)
        {
            return false;
        }

        if (!char.IsAsciiLetterOrDigit(name[0]) || !char.IsAsciiLetterOrDigit(name[^1]))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!char.IsAsciiDigit(character) &&
                !char.IsAsciiLetterLower(character) &&
                character is not '.' and not '-')
            {
                return false;
            }
        }

        return true;
    }

    public Result<ObjectStorageOptions> Configure(
        string? region = null,
        string? serviceUrl = null,
        bool usePathStyleAddressing = false,
        string? container = null,
        string? rootPrefix = null,
        string? localDownloadPath = null)
    {
        var normalizedRegion = Trim(region);
        if (normalizedRegion?.Length > 100)
        {
            return Failure("storage.region", "The region cannot exceed 100 characters.");
        }

        var normalizedServiceUrl = Trim(serviceUrl);
        if (normalizedServiceUrl is not null &&
            (!Uri.TryCreate(normalizedServiceUrl, UriKind.Absolute, out var parsed) ||
             (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
        {
            return Failure("storage.service_url", "The endpoint must be an absolute http or https URL.");
        }

        var normalizedContainer = Trim(container);
        if (normalizedContainer is not null && !IsValidContainerName(normalizedContainer))
        {
            return Failure(
                "storage.container",
                "The bucket or container name must be 3 to 63 characters of lower-case letters, digits, dots and hyphens, starting and ending with a letter or digit.");
        }

        var normalizedPrefix = Trim(rootPrefix?.Replace('\\', '/'))?.Trim('/');
        normalizedPrefix = string.IsNullOrEmpty(normalizedPrefix) ? null : normalizedPrefix;
        if (normalizedPrefix?.Length > 1_024)
        {
            return Failure("storage.root_prefix", "The prefix cannot exceed 1024 characters.");
        }

        Region = normalizedRegion;
        ServiceUrl = normalizedServiceUrl;
        UsePathStyleAddressing = usePathStyleAddressing;
        Container = normalizedContainer;
        RootPrefix = normalizedPrefix;
        LocalDownloadPath = Trim(localDownloadPath);
        return Result<ObjectStorageOptions>.Success(this);
    }

    private static Result<ObjectStorageOptions> Failure(string code, string message)
    {
        return Result<ObjectStorageOptions>.Failure(RemoteFlowError.Validation(code, message));
    }

    private static string? Trim(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
