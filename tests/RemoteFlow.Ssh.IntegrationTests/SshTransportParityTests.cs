using System.IO.Pipelines;
using System.Text;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Ssh;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Ssh.IntegrationTests;

[Collection<SshServerTestGroup>]
public sealed class SshTransportParityTests(SshServerFixture fixture)
{
    public static TheoryData<SshTransport> Transports =>
    [
        SshTransport.Tmds,
        SshTransport.SshNet,
    ];

    [Theory]
    [MemberData(nameof(Transports))]
    [Trait("Category", "Integration")]
    public async Task InteractiveShellAndResizeHaveIdenticalContract(SshTransport transportKind)
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(transportKind, PasswordRequest(), token);
        var opened = await connection.OpenShellAsync(new TerminalSpec { Columns = 80, Rows = 24 }, token);
        await using var shell = opened.Value;

        await shell.WriteAsync("printf 'parity-shell-ok\\n'\n"u8.ToArray(), token);
        Assert.Contains(
            "parity-shell-ok",
            await ReadUntilAsync(shell.Output, "parity-shell-ok", token),
            StringComparison.Ordinal);

        await shell.ResizeAsync(101, 41, token);
        await shell.WriteAsync("stty size\n"u8.ToArray(), token);
        Assert.Contains("41 101", await ReadUntilAsync(shell.Output, "41 101", token), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    [Trait("Category", "Integration")]
    public async Task ExecResultHasIdenticalContract(SshTransport transportKind)
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(transportKind, PasswordRequest(), token);

        var result = await connection.ExecuteAsync(
            "printf parity-out; printf parity-error >&2; exit 9",
            token);

        Assert.True(result.IsSuccess);
        Assert.Equal(9, result.Value.ExitCode);
        Assert.Equal("parity-out", result.Value.StandardOutput);
        Assert.Equal("parity-error", result.Value.StandardError);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    [Trait("Category", "Integration")]
    public async Task UnknownHostUsesSharedVerifierAndTypedFailure(SshTransport transportKind)
    {
        var result = await CreateTransport(transportKind, accepting: false).ConnectAsync(
            PasswordRequest() with { HostKeyPolicy = HostKeyPolicy.Strict },
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(SshError.HostKeyUnknown, result.Failure.Error);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    [Trait("Category", "Integration")]
    public async Task AuthenticationFailureUsesSameTypedResult(SshTransport transportKind)
    {
        var request = PasswordRequest() with
        {
            Authentication = new SshAuthMaterial.Password("wrong-password"),
        };

        var result = await CreateTransport(transportKind).ConnectAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(SshError.AuthFailed, result.Failure.Error);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    [Trait("Category", "Integration")]
    public async Task PrivateKeyAuthenticationHasIdenticalContract(SshTransport transportKind)
    {
        var token = TestContext.Current.CancellationToken;
        var request = PasswordRequest() with
        {
            Username = SshTestServer.PublicKeyUsername,
            Authentication = new SshAuthMaterial.PrivateKey(
                await fixture.Server.GetPrivateKeyAsync(token)),
        };
        await using var connection = await ConnectAsync(transportKind, request, token);

        var result = await connection.ExecuteAsync("printf key-parity", token);

        Assert.True(result.IsSuccess);
        Assert.Equal("key-parity", result.Value.StandardOutput);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    [Trait("Category", "Integration")]
    public async Task KeyboardInteractiveHasIdenticalPromptContract(SshTransport transportKind)
    {
        var token = TestContext.Current.CancellationToken;
        IReadOnlyList<SshAuthenticationPrompt>? presented = null;
        var request = PasswordRequest() with
        {
            Username = SshTestServer.KeyboardInteractiveUsername,
            Authentication = new SshAuthMaterial.KeyboardInteractive((prompts, _) =>
            {
                presented = prompts;
                return ValueTask.FromResult<IReadOnlyList<string>>([SshTestServer.KeyboardInteractivePassword]);
            }),
        };
        await using var connection = await ConnectAsync(transportKind, request, token);

        var result = await connection.ExecuteAsync("printf interactive-parity", token);

        Assert.Equal("interactive-parity", result.Value.StandardOutput);
        Assert.True(Assert.Single(presented!).IsSecret);
    }

    [Theory]
    [MemberData(nameof(Transports))]
    [Trait("Category", "Integration")]
    public async Task OperationTimeoutHasIdenticalTypedContract(SshTransport transportKind)
    {
        var token = TestContext.Current.CancellationToken;
        var request = PasswordRequest() with { OperationTimeout = TimeSpan.FromMilliseconds(100) };
        await using var connection = await ConnectAsync(transportKind, request, token);

        var timedOut = await connection.ExecuteAsync("sleep 2", token);
        var followUp = await connection.ExecuteAsync("printf usable-after-timeout", token);

        Assert.Equal(SshError.Timeout, timedOut.Failure.Error);
        Assert.True(followUp.IsSuccess);
        Assert.Equal("usable-after-timeout", followUp.Value.StandardOutput);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SshNetSftpImplementsTheApplicationContract()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(SshTransport.SshNet, PasswordRequest(), token);
        await using var sftp = connection.OpenSftp();
        var directory = $"/tmp/remoteflow-{Guid.NewGuid():N}";
        var original = $"{directory}/original.txt";
        var moved = $"{directory}/moved.txt";

        await sftp.CreateDirectoryAsync(directory, token);
        await using (var stream = await sftp.OpenWriteAsync(original, overwrite: true, token))
        {
            await stream.WriteAsync("sftp-ok"u8.ToArray(), token);
        }

        await using (var stream = await sftp.OpenReadAsync(original, token))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            Assert.Equal("sftp-ok", await reader.ReadToEndAsync(token));
        }

        Assert.Contains(await sftp.ListDirectoryAsync(directory, token), entry => entry.Name == "original.txt");
        await sftp.MoveAsync(original, moved, token);
        await sftp.DeleteAsync(moved, token);
        await sftp.DeleteAsync(directory, token);
    }

    private static async Task<ISshConnection> ConnectAsync(
        SshTransport transportKind,
        SshConnectRequest request,
        CancellationToken cancellationToken)
    {
        var result = await CreateTransport(transportKind).ConnectAsync(request, cancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Failure.Message : null);
        return result.Value;
    }

    private static ISshTransport CreateTransport(SshTransport transportKind, bool accepting = true)
    {
        var verifier = new HostKeyVerifier(
            new InMemoryHostKeyStore(),
            accepting ? new AcceptingPrompt() : new RejectingPrompt(),
            new FakeClock(new DateTimeOffset(2026, 8, 8, 1, 2, 3, TimeSpan.Zero)),
            new FakeGuidProvider());
        return transportKind switch
        {
            SshTransport.Tmds => new TmdsSshTransport(verifier),
            SshTransport.SshNet => new SshNetTransport(verifier),
            _ => throw new ArgumentOutOfRangeException(nameof(transportKind)),
        };
    }

    private SshConnectRequest PasswordRequest()
    {
        return new SshConnectRequest
        {
            Host = fixture.Server.Hostname,
            Port = fixture.Server.Port,
            Username = SshTestServer.PasswordUsername,
            Authentication = new SshAuthMaterial.Password(SshTestServer.Password),
            HostKeyPolicy = HostKeyPolicy.TrustOnFirstUse,
        };
    }

    private static async Task<string> ReadUntilAsync(
        PipeReader reader,
        string expected,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var output = new StringBuilder();
        while (!output.ToString().Contains(expected, StringComparison.Ordinal))
        {
            var result = await reader.ReadAsync(timeout.Token);
            foreach (var segment in result.Buffer)
            {
                _ = output.Append(Encoding.UTF8.GetString(segment.Span));
            }

            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }

        return output.ToString();
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
    }

    private sealed class RejectingPrompt : IHostKeyPrompt
    {
        public ValueTask<bool> ConfirmTrustAsync(
            HostKeyTrustPrompt prompt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(false);
        }
    }
}
