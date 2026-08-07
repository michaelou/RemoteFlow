using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Domain.ValueObjects;

public sealed class CredentialRef
{
    private CredentialRef()
    {
        Kind = CredentialKind.None;
        StoreKey = string.Empty;
        StoreProvider = string.Empty;
    }

    private CredentialRef(CredentialKind kind, string storeKey, string storeProvider, DateTimeOffset? updatedUtc)
    {
        Kind = kind;
        StoreKey = storeKey;
        StoreProvider = storeProvider;
        UpdatedUtc = updatedUtc?.ToUniversalTime();
    }

    public CredentialKind Kind { get; private set; }

    public string StoreKey { get; private set; }

    public string StoreProvider { get; private set; }

    public DateTimeOffset? UpdatedUtc { get; private set; }

    public bool IsEmpty => Kind == CredentialKind.None;

    public static CredentialRef None()
    {
        return new();
    }

    public static Result<CredentialRef> Create(
        CredentialKind kind,
        string? storeKey,
        string? storeProvider,
        DateTimeOffset? updatedUtc = null)
    {
        if (!Enum.IsDefined(kind) || kind == CredentialKind.None)
        {
            return Result<CredentialRef>.Failure(RemoteFlowError.Validation(
                "credential.kind",
                "A persisted credential must have a concrete credential kind."));
        }

        var normalizedKey = DomainValidation.Required(storeKey, 512, "credential.store_key", out var error);
        if (error is not null)
        {
            return Result<CredentialRef>.Failure(error);
        }

        var normalizedProvider = DomainValidation.Required(storeProvider, 100, "credential.store_provider", out error);
        return error is not null
            ? Result<CredentialRef>.Failure(error)
            : Result<CredentialRef>.Success(new CredentialRef(kind, normalizedKey!, normalizedProvider!, updatedUtc));
    }
}
