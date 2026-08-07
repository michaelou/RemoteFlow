using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Ssh.IntegrationTests;

[Collection<SshServerTestGroup>]
public sealed class SshServerHarnessTests(SshServerFixture fixture)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PasswordUserAuthenticates()
    {
        var result = await fixture.Server.ExecAsync(
        [
            "sshpass",
            "-p",
            SshTestServer.Password,
            "ssh",
            "-o",
            "StrictHostKeyChecking=no",
            "-o",
            "UserKnownHostsFile=/dev/null",
            "-o",
            "PreferredAuthentications=password",
            $"{SshTestServer.PasswordUsername}@localhost",
            "printf password-ok",
        ], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("password-ok", result.Stdout);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AuthorizedKeyUserAuthenticates()
    {
        var result = await fixture.Server.ExecAsync(
        [
            "ssh",
            "-i",
            "/opt/remoteflow/client_keys/id_ed25519",
            "-o",
            "StrictHostKeyChecking=no",
            "-o",
            "UserKnownHostsFile=/dev/null",
            "-o",
            "PreferredAuthentications=publickey",
            $"{SshTestServer.PublicKeyUsername}@localhost",
            "printf key-ok",
        ], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("key-ok", result.Stdout);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task KeyboardInteractiveUserAuthenticates()
    {
        var result = await fixture.Server.ExecAsync(
        [
            "sshpass",
            "-p",
            SshTestServer.KeyboardInteractivePassword,
            "ssh",
            "-o",
            "StrictHostKeyChecking=no",
            "-o",
            "UserKnownHostsFile=/dev/null",
            "-o",
            "PreferredAuthentications=keyboard-interactive",
            $"{SshTestServer.KeyboardInteractiveUsername}@localhost",
            "printf interactive-ok",
        ], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("interactive-ok", result.Stdout);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HostKeyCanBeSwappedAndRestored()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.Server.UseHostKeyAsync(SshTestHostKey.Primary, token);
        var primary = await fixture.Server.GetHostKeyFingerprintAsync(token);
        try
        {
            await fixture.Server.UseHostKeyAsync(SshTestHostKey.Alternate, token);
            var alternate = await fixture.Server.GetHostKeyFingerprintAsync(token);
            Assert.NotEqual(primary, alternate);
        }
        finally
        {
            await fixture.Server.UseHostKeyAsync(SshTestHostKey.Primary, token);
        }

        Assert.Equal(primary, await fixture.Server.GetHostKeyFingerprintAsync(token));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SftpFixtureTreeIsPresentAndTestsCanCleanTheirOwnState()
    {
        var token = TestContext.Current.CancellationToken;
        var testDirectory = $"/tmp/remoteflow-{Guid.NewGuid():N}";
        try
        {
            var fixtureResult = await fixture.Server.ExecAsync(
            [
                "test",
                "-f",
                $"{SshTestServer.FixtureRoot}/documents/readme.txt",
            ], token);
            var createResult = await fixture.Server.ExecAsync(
            [
                "mkdir",
                testDirectory,
            ], token);

            Assert.Equal(0, fixtureResult.ExitCode);
            Assert.Equal(0, createResult.ExitCode);
        }
        finally
        {
            _ = await fixture.Server.ExecAsync(
            [
                "rm",
                "-rf",
                testDirectory,
            ], token);
        }
    }
}
