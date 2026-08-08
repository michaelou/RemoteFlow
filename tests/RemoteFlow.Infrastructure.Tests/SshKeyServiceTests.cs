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

    [Fact]
    public async Task DiscoveryListsPrivateKeysAndSkipsTheRestOfTheSshFolder()
    {
        var token = TestContext.Current.CancellationToken;
        using var home = TemporaryDirectory.Create();
        var service = CreateService(home.Path, out var sshDirectory);
        _ = await service.GenerateEd25519Async(Path.Combine(sshDirectory, "id_ed25519"), "discovered", token);
        await File.WriteAllTextAsync(Path.Combine(sshDirectory, "known_hosts"), "example.test ssh-ed25519 AAAA\n", token);
        await File.WriteAllTextAsync(Path.Combine(sshDirectory, "config"), "Host *\n", token);
        await File.WriteAllTextAsync(Path.Combine(sshDirectory, "notes.txt"), "not a key\n", token);

        var discovered = await service.DiscoverAsync(token);

        var key = Assert.Single(discovered);
        Assert.Equal(Path.Combine(sshDirectory, "id_ed25519"), key.Path);
        Assert.Equal("ssh-ed25519", key.KeyType);
    }

    [Fact]
    public async Task PastedKeyIsWrittenIntoTheSshFolderAndInspected()
    {
        var token = TestContext.Current.CancellationToken;
        using var home = TemporaryDirectory.Create();
        using var source = TemporaryDirectory.Create();
        var service = CreateService(home.Path, out var sshDirectory);
        var generated = Path.Combine(source.Path, "id_ed25519");
        var original = await service.GenerateEd25519Async(generated, "pasted", token);
        var text = await File.ReadAllTextAsync(generated, token);

        var imported = await service.ImportAsync(Path.Combine(sshDirectory, "id_pasted"), text, token);

        Assert.Equal(Path.Combine(sshDirectory, "id_pasted"), imported.Path);
        Assert.Equal(original.Sha256Fingerprint, imported.Sha256Fingerprint);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(imported.Path));
        }
    }

    [Fact]
    public async Task PastingAPublicKeySaysWhichFileIsActuallyNeeded()
    {
        var token = TestContext.Current.CancellationToken;
        using var home = TemporaryDirectory.Create();
        var service = CreateService(home.Path, out var sshDirectory);

        var exception = await Assert.ThrowsAsync<SshKeyFormatException>(
            () => service.ImportAsync(
                Path.Combine(sshDirectory, "id_public"),
                "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5 user@host",
                token));

        Assert.Contains("public key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(sshDirectory, "id_public")));
    }

    private static SshKeyService CreateService(string home, out string sshDirectory)
    {
        sshDirectory = Path.Combine(home, ".ssh");
        _ = Directory.CreateDirectory(sshDirectory);
        return new SshKeyService(platform: new FakeHome(home));
    }

    private sealed class FakeHome(string home) : ISystemPlatform
    {
        public OperatingSystemFamily OperatingSystem { get; } =
            System.OperatingSystem.IsWindows() ? OperatingSystemFamily.Windows : OperatingSystemFamily.Linux;

        public string CurrentDirectory => home;

        public string HomeDirectory => home;

        public string? GetEnvironmentVariable(string name)
        {
            return null;
        }

        public string? FindExecutable(string name)
        {
            return null;
        }

        public bool FileExists(string path)
        {
            return File.Exists(path);
        }

        public string? GetLoginShellFromPasswd()
        {
            return null;
        }
    }
}
