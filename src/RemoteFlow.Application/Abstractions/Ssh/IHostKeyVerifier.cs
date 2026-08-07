using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Abstractions.Ssh;

public sealed record HostKeyVerificationRequest(
    string Host,
    int Port,
    HostKeyInfo HostKey,
    HostKeyPolicy Policy);

public sealed record HostKeyTrustPrompt(
    string Host,
    int Port,
    string KeyAlgorithm,
    string Sha256Fingerprint);

public sealed record HostKeyVerificationResult(
    string Sha256Fingerprint,
    bool IsFlagged);

public interface IHostKeyPrompt
{
    ValueTask<bool> ConfirmTrustAsync(
        HostKeyTrustPrompt prompt,
        CancellationToken cancellationToken = default);
}

public interface IHostKeyVerifier
{
    Task<SshResult<HostKeyVerificationResult>> VerifyAsync(
        HostKeyVerificationRequest request,
        CancellationToken cancellationToken = default);
}
