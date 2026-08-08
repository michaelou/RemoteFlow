using System.IO.Pipelines;
using System.Net.NetworkInformation;
using System.Text;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Infrastructure.Ssh;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Ssh.IntegrationTests;

[Collection<SshServerTestGroup>]
public sealed class TmdsSshTransportTests(SshServerFixture fixture)
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task InteractiveShellTypesReadsAndPropagatesResize()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(PasswordRequest(), token);
        var opened = await connection.OpenShellAsync(new TerminalSpec
        {
            Columns = 80,
            Rows = 24,
        }, token);
        await using var shell = opened.Value;

        await shell.WriteAsync("printf 'shell-ok\\n'\n"u8.ToArray(), token);
        Assert.Contains("shell-ok", await ReadUntilAsync(shell.Output, "shell-ok", token), StringComparison.Ordinal);

        await shell.ResizeAsync(93, 37, token);
        await shell.WriteAsync("stty size\n"u8.ToArray(), token);
        Assert.Contains("37 93", await ReadUntilAsync(shell.Output, "37 93", token), StringComparison.Ordinal);

        await shell.WriteAsync("exit\n"u8.ToArray(), token);
        Assert.Equal(0, await shell.Exited.WaitAsync(TimeSpan.FromSeconds(10), token));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExecCapturesOutputErrorAndExitCode()
    {
        var token = TestContext.Current.CancellationToken;
        var request = await PrivateKeyRequestAsync(token);
        await using var connection = await ConnectAsync(request, token);

        var result = await connection.ExecuteAsync(
            "printf stdout-ok; printf stderr-ok >&2; exit 7",
            token);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value.ExitCode);
        Assert.Equal("stdout-ok", result.Value.StandardOutput);
        Assert.Equal("stderr-ok", result.Value.StandardError);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task OperationTimeoutIsTypedAndConnectionRemainsUsable()
    {
        var token = TestContext.Current.CancellationToken;
        var request = PasswordRequest() with { OperationTimeout = TimeSpan.FromMilliseconds(100) };
        await using var connection = await ConnectAsync(request, token);

        var timedOut = await connection.ExecuteAsync("sleep 2", token);
        var followUp = await connection.ExecuteAsync("printf still-usable", token);

        Assert.Equal(SshError.Timeout, timedOut.Failure.Error);
        Assert.True(followUp.IsSuccess);
        Assert.Equal("still-usable", followUp.Value.StandardOutput);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FullScreenTerminalProgramsAreInstalledOnPtyServer()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = await ConnectAsync(PasswordRequest(), token);

        var result = await connection.ExecuteAsync(
            "(command -v vim || command -v vim.tiny); command -v tmux; command -v htop",
            token);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.ExitCode);
        Assert.Contains("vim", result.Value.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("tmux", result.Value.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("htop", result.Value.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AuthenticationFailureIsTypedResult()
    {
        var token = TestContext.Current.CancellationToken;
        var request = PasswordRequest() with
        {
            Authentication = new SshAuthMaterial.Password("wrong-password"),
        };
        var transport = CreateTransport();

        var result = await transport.ConnectAsync(request, token);

        Assert.True(result.IsFailure);
        Assert.Equal(SshError.AuthFailed, result.Failure.Error);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task KeyboardInteractiveAuthenticatesAndPreservesSecretEchoFlag()
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

        await using var connection = await ConnectAsync(request, token);
        var result = await connection.ExecuteAsync("printf interactive-ok", token);

        Assert.Equal("interactive-ok", result.Value.StandardOutput);
        Assert.NotNull(presented);
        Assert.True(Assert.Single(presented).IsSecret);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MissingAgentFallsBackToPassword()
    {
        var token = TestContext.Current.CancellationToken;
        var request = PasswordRequest() with
        {
            AuthenticationMethods =
            [
                new SshAuthMaterial.Agent(),
                new SshAuthMaterial.Password(SshTestServer.Password),
            ],
        };

        await using var connection = await ConnectAsync(request, token);
        var result = await connection.ExecuteAsync("printf fallback-ok", token);

        Assert.Equal("fallback-ok", result.Value.StandardOutput);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EncryptedPrivateKeyAuthenticatesWithPassphrase()
    {
        var token = TestContext.Current.CancellationToken;
        var request = PasswordRequest() with
        {
            Username = SshTestServer.PublicKeyUsername,
            Authentication = new SshAuthMaterial.PrivateKey(
                await fixture.Server.GetEncryptedPrivateKeyAsync(token),
                SshTestServer.EncryptedPrivateKeyPassphrase),
        };

        await using var connection = await ConnectAsync(request, token);
        var result = await connection.ExecuteAsync("printf encrypted-key-ok", token);

        Assert.Equal("encrypted-key-ok", result.Value.StandardOutput);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task KeyboardInteractiveRetryLimitStopsPromptLoop()
    {
        var token = TestContext.Current.CancellationToken;
        var attempts = 0;
        var request = PasswordRequest() with
        {
            Username = SshTestServer.KeyboardInteractiveUsername,
            MaxAuthenticationAttempts = 2,
            Authentication = new SshAuthMaterial.KeyboardInteractive((_, _) =>
            {
                _ = Interlocked.Increment(ref attempts);
                return ValueTask.FromResult<IReadOnlyList<string>>(["wrong-secret"]);
            }),
        };

        var result = await CreateTransport().ConnectAsync(request, token);

        Assert.Equal(SshError.AuthFailed, result.Failure.Error);
        Assert.InRange(attempts, 1, 2);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StrictUnknownHostIsTypedAndConnectionIsNotEstablished()
    {
        var transport = CreateTransport();

        var result = await transport.ConnectAsync(PasswordRequest() with
        {
            HostKeyPolicy = HostKeyPolicy.Strict,
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(SshError.HostKeyUnknown, result.Failure.Error);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ChangedLiveHostKeyIsTypedAndConnectionIsNotEstablished()
    {
        var token = TestContext.Current.CancellationToken;
        await fixture.Server.UseHostKeyAsync(SshTestHostKey.Primary, token);
        var transport = CreateTransport();
        var request = PasswordRequest();
        var first = await transport.ConnectAsync(request, token);
        Assert.True(first.IsSuccess);
        await first.Value.DisposeAsync();

        try
        {
            await fixture.Server.UseHostKeyAsync(SshTestHostKey.Alternate, token);
            var changed = await transport.ConnectAsync(request, token);

            Assert.True(changed.IsFailure);
            Assert.Equal(SshError.HostKeyMismatch, changed.Failure.Error);
        }
        finally
        {
            await fixture.Server.UseHostKeyAsync(SshTestHostKey.Primary, token);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DisconnectEventRaisesExactlyOnceAcrossRepeatedDisposal()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = await ConnectAsync(PasswordRequest(), token);
        var raised = 0;
        connection.Disconnected += (_, _) => Interlocked.Increment(ref raised);

        await connection.DisposeAsync();
        await connection.DisposeAsync();

        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DisposingConnectionCompletesPendingShellReadWithoutObjectDisposedFailure()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = await ConnectAsync(PasswordRequest(), token);
        var shell = (await connection.OpenShellAsync(new TerminalSpec(), token)).Value;

        await connection.DisposeAsync();
        var exitCode = await shell.Exited.WaitAsync(TimeSpan.FromSeconds(10), token);
        await shell.DisposeAsync();

        Assert.Null(exitCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CancellingStalledHandshakeCompletesAndLeavesNoActiveSocket()
    {
        var token = TestContext.Current.CancellationToken;
        var baseline = CountActiveConnections(fixture.Server.StalledPort);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(250));
        var stalledRequest = PasswordRequest() with { Port = fixture.Server.StalledPort };

        var result = await CreateTransport().ConnectAsync(stalledRequest, cancellation.Token)
            .WaitAsync(TimeSpan.FromSeconds(5), token);

        Assert.True(result.IsFailure);
        Assert.Equal(SshError.Cancelled, result.Failure.Error);

        await WaitUntilAsync(
            () => CountActiveConnections(fixture.Server.StalledPort) <= baseline,
            TimeSpan.FromSeconds(5),
            token);
        await using var connection = await ConnectAsync(PasswordRequest(), token);
    }

    private static async Task<ISshConnection> ConnectAsync(
        SshConnectRequest request,
        CancellationToken cancellationToken)
    {
        var result = await CreateTransport().ConnectAsync(request, cancellationToken);
        Assert.True(result.IsSuccess, result.IsFailure ? result.Failure.Message : null);
        return result.Value;
    }

    private static TmdsSshTransport CreateTransport()
    {
        var verifier = new HostKeyVerifier(
            new InMemoryHostKeyStore(),
            new AcceptingPrompt(),
            new FakeClock(new DateTimeOffset(2026, 8, 8, 1, 2, 3, TimeSpan.Zero)),
            new FakeGuidProvider());
        return new TmdsSshTransport(verifier);
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

    private async Task<SshConnectRequest> PrivateKeyRequestAsync(CancellationToken cancellationToken)
    {
        return new SshConnectRequest
        {
            Host = fixture.Server.Hostname,
            Port = fixture.Server.Port,
            Username = SshTestServer.PublicKeyUsername,
            Authentication = new SshAuthMaterial.PrivateKey(
                await fixture.Server.GetPrivateKeyAsync(cancellationToken)),
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

    private static int CountActiveConnections(int port)
    {
        return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Count(connection =>
            connection.RemoteEndPoint.Port == port &&
            connection.State is not TcpState.Closed and not TcpState.TimeWait);
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        while (!predicate())
        {
            await Task.Delay(50, timeoutSource.Token);
        }
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
