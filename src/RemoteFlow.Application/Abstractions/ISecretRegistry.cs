namespace RemoteFlow.Application.Abstractions;

public interface ISecretRegistry
{
    void Register(string secret);

    IReadOnlyList<string> GetSecrets();
}
