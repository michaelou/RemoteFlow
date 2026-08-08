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
    string Sha256Fingerprint,
    string? StoredSha256Fingerprint = null,
    string? RandomArt = null)
{
    public bool IsMismatch => StoredSha256Fingerprint is not null;
}

public enum HostKeyPromptDecision
{
    Reject = 0,
    AcceptOnce = 1,
    AcceptAndSave = 2,
}

public sealed record HostKeyVerificationResult(
    string Sha256Fingerprint,
    bool IsFlagged);

public interface IHostKeyPrompt
{
    ValueTask<bool> ConfirmTrustAsync(
        HostKeyTrustPrompt prompt,
        CancellationToken cancellationToken = default);

    async ValueTask<HostKeyPromptDecision> PromptAsync(
        HostKeyTrustPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        return await ConfirmTrustAsync(prompt, cancellationToken).ConfigureAwait(false)
            ? HostKeyPromptDecision.AcceptAndSave
            : HostKeyPromptDecision.Reject;
    }
}

public interface IHostKeyVerifier
{
    Task<SshResult<HostKeyVerificationResult>> VerifyAsync(
        HostKeyVerificationRequest request,
        CancellationToken cancellationToken = default);
}
