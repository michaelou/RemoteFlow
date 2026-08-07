using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Domain.Entities;

public sealed class HostKey
{
    private HostKey()
    {
        Host = string.Empty;
        KeyAlgorithm = string.Empty;
        PublicKeyBase64 = string.Empty;
        Sha256Fingerprint = string.Empty;
    }

    public Guid Id { get; private set; }

    public string Host { get; private set; }

    public int Port { get; private set; }

    public string KeyAlgorithm { get; private set; }

    public string PublicKeyBase64 { get; private set; }

    public string Sha256Fingerprint { get; private set; }

    public HostKeyTrust TrustState { get; private set; }

    public HostKeySource Source { get; private set; }

    public string? Comment { get; private set; }

    public DateTimeOffset FirstSeenUtc { get; private set; }

    public DateTimeOffset LastSeenUtc { get; private set; }

    public static Result<HostKey> Create(
        IGuidProvider guidProvider,
        string? host,
        int port,
        string? keyAlgorithm,
        string? publicKeyBase64,
        string? sha256Fingerprint,
        HostKeyTrust trustState,
        HostKeySource source,
        string? comment = null,
        DateTimeOffset? seenUtc = null)
    {
        var normalizedHost = DomainValidation.Required(host, 255, "host_key.host", out var error);
        if (error is not null)
        {
            return Result<HostKey>.Failure(error);
        }

        if (port is < 1 or > 65_535)
        {
            return Result<HostKey>.Failure(RemoteFlowError.Validation("host_key.port", "The port must be between 1 and 65535."));
        }

        var normalizedAlgorithm = DomainValidation.Required(keyAlgorithm, 100, "host_key.algorithm", out error);
        if (error is not null)
        {
            return Result<HostKey>.Failure(error);
        }

        var normalizedKey = DomainValidation.Required(publicKeyBase64, 16_384, "host_key.public_key", out error);
        if (error is not null)
        {
            return Result<HostKey>.Failure(error);
        }

        var normalizedFingerprint = DomainValidation.Required(sha256Fingerprint, 200, "host_key.fingerprint", out error);
        if (error is not null || !normalizedFingerprint!.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            return Result<HostKey>.Failure(error ?? RemoteFlowError.Validation(
                "host_key.fingerprint",
                "The fingerprint must use the SHA256: format."));
        }

        if (!Enum.IsDefined(trustState) || !Enum.IsDefined(source))
        {
            return Result<HostKey>.Failure(RemoteFlowError.Validation("host_key.state", "The trust state or source is invalid."));
        }

        var seen = DomainValidation.Utc(seenUtc ?? DateTimeOffset.UtcNow);
        return Result<HostKey>.Success(new HostKey
        {
            Id = DomainValidation.NewRequiredGuid(guidProvider),
            Host = normalizedHost!,
            Port = port,
            KeyAlgorithm = normalizedAlgorithm!,
            PublicKeyBase64 = normalizedKey!,
            Sha256Fingerprint = normalizedFingerprint,
            TrustState = trustState,
            Source = source,
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
            FirstSeenUtc = seen,
            LastSeenUtc = seen,
        });
    }

    public HostKey Observe(DateTimeOffset seenUtc)
    {
        var normalized = DomainValidation.Utc(seenUtc);
        if (normalized > LastSeenUtc)
        {
            LastSeenUtc = normalized;
        }

        return this;
    }

    public HostKey SetTrust(HostKeyTrust trustState, HostKeySource source, string? comment = null)
    {
        if (!Enum.IsDefined(trustState))
        {
            throw new ArgumentOutOfRangeException(nameof(trustState));
        }

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        TrustState = trustState;
        Source = source;
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        return this;
    }
}
