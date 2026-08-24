namespace RemoteFlow.Application.Abstractions;

public sealed record VaultUnlockPromptRequest
{
    /// <summary>True when no vault exists yet, so the user is inventing a passphrase rather than recalling
    /// one. The two are different enough — confirmation box, strength rule, no "that was wrong" — that the
    /// prompt has to know which it is asking for.</summary>
    public required bool IsNewVault { get; init; }

    /// <summary>1 for the first ask. Only ever above 1 for an existing vault.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>Why the previous attempt did not work, when there was one.</summary>
    public string? Problem { get; init; }
}

public sealed record VaultUnlockPromptResult(SecretHandle Secret) : IDisposable
{
    public void Dispose()
    {
        Secret.Dispose();
    }
}

/// <summary>Asks the user for the vault passphrase. Returns null when they decline, which is a real answer
/// and not an error: RemoteFlow runs without a credential store, it just cannot remember secrets.</summary>
public interface IVaultUnlockPrompt
{
    ValueTask<VaultUnlockPromptResult?> PromptAsync(
        VaultUnlockPromptRequest request,
        CancellationToken cancellationToken = default);
}
