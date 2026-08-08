using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Ssh.Auth;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class SshKeyServiceTests
{
    [Fact]
    public async Task PpkIsRefusedWithPuttygenInstruction()
    {
        var token = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "legacy.ppk");
        await File.WriteAllTextAsync(path, "PuTTY-User-Key-File-3: ssh-ed25519\n", token);

        var exception = await Assert.ThrowsAsync<SshKeyFormatException>(
            () => new SshKeyService().InspectAsync(path, cancellationToken: token));

        Assert.Contains("puttygen key.ppk -O private-openssh -o key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncryptedPkcs8IsDetectedBeforePassphrasePrompt()
    {
        var token = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "encrypted.pem");
        await File.WriteAllTextAsync(
            path,
            "-----BEGIN ENCRYPTED PRIVATE KEY-----\nAA==\n-----END ENCRYPTED PRIVATE KEY-----\n",
            token);

        var result = await new SshKeyService().InspectAsync(path, cancellationToken: token);

        Assert.Equal(SshPrivateKeyFormat.Pkcs8, result.Format);
        Assert.True(result.IsEncrypted);
        Assert.Null(result.Sha256Fingerprint);
    }

    [Fact]
    public async Task GeneratedEd25519PairHasPublicIdentityAndPrivateMode()
    {
        var token = TestContext.Current.CancellationToken;
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "id_ed25519");

        var result = await new SshKeyService().GenerateEd25519Async(path, "remoteflow-test", token);

        Assert.Equal("ssh-ed25519", result.KeyType);
        Assert.StartsWith("SHA256:", result.Sha256Fingerprint, StringComparison.Ordinal);
        Assert.Contains("remoteflow-test", await File.ReadAllTextAsync(path + ".pub", token), StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
        }
    }
}
