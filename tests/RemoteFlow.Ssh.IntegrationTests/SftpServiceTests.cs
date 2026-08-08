using System.Diagnostics;
using System.Text;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Ssh;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Ssh.IntegrationTests;

[Collection<SshServerTestGroup>]
public sealed class SftpServiceTests(SshServerFixture fixture)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task NamesMetadataSymlinksAndEveryOperationRoundTrip()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(token);
        await using var sftp = connection.OpenSftp();
        var directory = $"/tmp/remoteflow-sftp-{Guid.NewGuid():N}";
        var names = new[]
        {
            "UTF-8-文件-🚀.txt",
            "spaces 'single' \"double\".txt",
            "literal\nnewline.txt",
            new string('x', 255),
        };

        try
        {
            Assert.True((await sftp.CreateDirectoryAsync(directory, token)).IsSuccess);
            foreach (var name in names)
            {
                var path = SftpPath.Combine(directory, name);
                await using var stream = (await sftp.OpenWriteAsync(path, token)).Value;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(name), token);
            }

            var link = SftpPath.Combine(directory, "readme-link");
            var target = SftpPath.Combine(directory, names[0]);
            var linked = await connection.ExecuteAsync(
                $"ln -s {SftpPath.ToShellLiteral(target)} {SftpPath.ToShellLiteral(link)}",
                token);
            Assert.True(linked.IsSuccess);
            Assert.Equal(0, linked.Value.ExitCode);

            var listed = await sftp.ListAsync(directory + "/./", token);
            Assert.True(listed.IsSuccess);
            Assert.All(names, name => Assert.Contains(listed.Value, entry => entry.Name == name));
            var linkInfo = Assert.Single(listed.Value, entry => entry.Name == "readme-link");
            Assert.True(linkInfo.IsSymlink);
            Assert.False(linkInfo.IsDirectory);
            Assert.Equal(target, linkInfo.SymlinkTarget);

            var original = SftpPath.Combine(directory, names[0]);
            var stat = await sftp.StatAsync(original, token);
            Assert.True(stat.IsSuccess);
            Assert.Equal(Encoding.UTF8.GetByteCount(names[0]), stat.Value!.Size);

            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
            Assert.True((await sftp.SetPermissionsAsync(original, mode, token)).IsSuccess);
            Assert.Equal(mode, (await sftp.StatAsync(original, token)).Value!.Mode);

            var renamed = SftpPath.Combine(directory, "renamed.txt");
            Assert.True((await sftp.RenameAsync(original, renamed, token)).IsSuccess);
            var missing = await sftp.StatAsync(original, token);
            Assert.True(missing.IsSuccess, missing.IsFailure ? missing.Failure.Message : "Stat unexpectedly failed.");
            Assert.Null(missing.Value);
            Assert.NotNull((await sftp.StatAsync(renamed, token)).Value);
        }
        finally
        {
            Assert.True((await sftp.DeleteAsync(directory, recursive: true, token)).IsSuccess);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PathsPermissionFailuresAndDisposedStreamsAreSafe()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(token);
        await using var sftp = connection.OpenSftp();
        var directory = $"/tmp/remoteflow-sftp-{Guid.NewGuid():N}";
        var child = SftpPath.Combine(directory, "child");
        var file = SftpPath.Combine(directory, "payload.bin");
        try
        {
            Assert.Equal("/alpha/gamma", SftpPath.Normalize("\\alpha\\beta\\..\\gamma\\.\\"));
            Assert.True((await sftp.CreateDirectoryAsync(directory, token)).IsSuccess);
            Assert.True((await sftp.CreateDirectoryAsync(child, token)).IsSuccess);
            var mixedPath = directory.Replace('/', '\\') + "\\child\\..\\payload.bin";
            await using (var write = (await sftp.OpenWriteAsync(mixedPath, token)).Value)
            {
                await write.WriteAsync(new byte[256 * 1024], token);
            }

            var opened = await sftp.OpenReadAsync(file, token);
            Assert.True(opened.IsSuccess);
            var buffer = new byte[8192];
            _ = await opened.Value.ReadAsync(buffer, token);
            await opened.Value.DisposeAsync();
            Assert.True((await sftp.ListAsync(directory, token)).IsSuccess);

            var denied = SftpPath.Combine(directory, "denied");
            Assert.True((await sftp.CreateDirectoryAsync(denied, token)).IsSuccess);
            Assert.True((await sftp.SetPermissionsAsync(denied, default, token)).IsSuccess);
            var failure = await sftp.ListAsync(denied, token);
            Assert.True(failure.IsFailure);
            Assert.Equal(SftpError.PermissionDenied, failure.Failure.Error);

            var home = await sftp.GetRealPathAsync("~", token);
            var relative = await sftp.GetRealPathAsync(".", token);
            Assert.True(home.IsSuccess);
            Assert.True(relative.IsSuccess);
            Assert.StartsWith("/home/", home.Value, StringComparison.Ordinal);
            Assert.Equal(home.Value, relative.Value);
        }
        finally
        {
            _ = await fixture.Server.ExecAsync(["chmod", "0700", SftpPath.Combine(directory, "denied")], token);
            _ = await fixture.Server.ExecAsync(["rm", "-rf", directory], token);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FiveThousandEntriesListWithinFifteenSeconds()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(token);
        await using var sftp = connection.OpenSftp();
        var directory = $"/tmp/remoteflow-sftp-{Guid.NewGuid():N}";
        try
        {
            var created = await connection.ExecuteAsync(
                $"mkdir {SftpPath.ToShellLiteral(directory)} && " +
                $"i=1; while [ $i -le 5000 ]; do : > {SftpPath.ToShellLiteral(directory)}/file-$i; i=$((i+1)); done",
                token);
            Assert.True(created.IsSuccess);
            Assert.Equal(0, created.Value.ExitCode);

            var stopwatch = Stopwatch.StartNew();
            var result = await sftp.ListAsync(directory, token);
            stopwatch.Stop();

            Assert.True(result.IsSuccess);
            Assert.Equal(5000, result.Value.Count);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Listing took {stopwatch.Elapsed}.");
        }
        finally
        {
            _ = await fixture.Server.ExecAsync(["rm", "-rf", directory], token);
        }
    }

    private async Task<ISshConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var verifier = new HostKeyVerifier(
            new InMemoryHostKeyStore(),
            new AcceptingPrompt(),
            new FakeClock(new DateTimeOffset(2026, 8, 8, 1, 2, 3, TimeSpan.Zero)),
            new FakeGuidProvider());
        var result = await new TmdsSshTransport(verifier).ConnectAsync(new SshConnectRequest
        {
            Host = fixture.Server.Hostname,
            Port = fixture.Server.Port,
            Username = SshTestServer.PasswordUsername,
            Authentication = new SshAuthMaterial.Password(SshTestServer.Password),
            HostKeyPolicy = HostKeyPolicy.TrustOnFirstUse,
        }, cancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Failure.Message : null);
        return result.Value;
    }

    private sealed class AcceptingPrompt : IHostKeyPrompt
    {
        public ValueTask<bool> ConfirmTrustAsync(
            HostKeyTrustPrompt prompt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }

        public ValueTask<HostKeyPromptDecision> PromptAsync(
            HostKeyTrustPrompt prompt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                prompt.IsMismatch ? HostKeyPromptDecision.Reject : HostKeyPromptDecision.AcceptAndSave);
        }
    }
}
