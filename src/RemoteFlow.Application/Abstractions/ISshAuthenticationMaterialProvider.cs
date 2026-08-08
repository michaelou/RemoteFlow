using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Abstractions;

public sealed record SshCredentialPromptRequest(
    string Title,
    string Message,
    CredentialKind Kind,
    bool AllowSave);

public sealed record SshCredentialPromptResult(SecretHandle Secret, bool Save) : IDisposable
{
    public void Dispose()
    {
        Secret.Dispose();
    }
}

public interface ISshCredentialPrompt
{
    ValueTask<SshCredentialPromptResult?> PromptAsync(
        SshCredentialPromptRequest request,
        CancellationToken cancellationToken = default);
}

public interface ISshAuthenticationMaterialProvider
{
    Task<IReadOnlyList<SshAuthMaterial>> CreateAsync(
        Connection connection,
        CancellationToken cancellationToken = default);
}
