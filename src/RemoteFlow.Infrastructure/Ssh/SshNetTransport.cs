using System.Text;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace RemoteFlow.Infrastructure.Ssh;

public sealed class SshNetTransport(
    IHostKeyVerifier hostKeyVerifier,
    ISecretRegistry? secretRegistry = null) : ISshTransport
{
    private readonly IHostKeyVerifier _hostKeyVerifier =
        hostKeyVerifier ?? throw new ArgumentNullException(nameof(hostKeyVerifier));
    private readonly ISecretRegistry? _secretRegistry = secretRegistry;

    public async Task<SshResult<ISshConnection>> ConnectAsync(
        SshConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var configuredAuthentication = request.AuthenticationMethods.Count > 0
            ? request.AuthenticationMethods
            : [request.Authentication];
        if (configuredAuthentication.All(authentication => authentication is SshAuthMaterial.Agent))
        {
            return SshResult<ISshConnection>.Fail(
                SshError.AuthFailed,
                "SSH.NET cannot use the platform SSH agent. Configure a password or private key, or select Tmds.Ssh.");
        }

        var verification = new VerificationState();
        var client = CreateClient(request, verification, cancellationToken);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return SshResult<ISshConnection>.Success(new SshNetConnection(
                client,
                request.OperationTimeout,
                token => CreateSftpClient(request, new VerificationState(), token)));
        }
        catch (Exception exception)
        {
            client.Dispose();
            return verification.Failure is not null
                ? SshResult<ISshConnection>.Fail(verification.Failure.Error, verification.Failure.Message)
                : SshErrorMapper.Failure<ISshConnection>(exception, cancellationToken);
        }
    }

    private SshClient CreateClient(
        SshConnectRequest request,
        VerificationState verification,
        CancellationToken cancellationToken)
    {
        var connectionInfo = CreateConnectionInfo(request, cancellationToken);
        var client = new SshClient(connectionInfo)
        {
            KeepAliveInterval = request.KeepAliveInterval,
        };
        client.HostKeyReceived += (_, eventArgs) =>
            VerifyHostKey(request, verification, eventArgs, cancellationToken);
        return client;
    }

    private SftpClient CreateSftpClient(
        SshConnectRequest request,
        VerificationState verification,
        CancellationToken cancellationToken)
    {
        var client = new SftpClient(CreateConnectionInfo(request, cancellationToken))
        {
            OperationTimeout = request.OperationTimeout,
        };
        client.HostKeyReceived += (_, eventArgs) =>
            VerifyHostKey(request, verification, eventArgs, cancellationToken);
        return client;
    }

    private ConnectionInfo CreateConnectionInfo(
        SshConnectRequest request,
        CancellationToken cancellationToken)
    {
        var configured = request.AuthenticationMethods.Count > 0
            ? request.AuthenticationMethods
            : [request.Authentication];
        var methods = configured
            .Where(authentication => authentication is not SshAuthMaterial.Agent)
            .Select(authentication => CreateAuthenticationMethod(request.Username, authentication, cancellationToken))
            .ToArray();
        if (methods.Length == 0)
        {
            methods = [new NoneAuthenticationMethod(request.Username)];
        }

        return new ConnectionInfo(request.Host, request.Port, request.Username, methods)
        {
            Timeout = request.ConnectTimeout,
            RetryAttempts = request.MaxAuthenticationAttempts,
        };
    }

    private AuthenticationMethod CreateAuthenticationMethod(
        string username,
        SshAuthMaterial authentication,
        CancellationToken cancellationToken)
    {
        switch (authentication)
        {
            case SshAuthMaterial.None:
                return new NoneAuthenticationMethod(username);
            case SshAuthMaterial.Password password:
                Register(password.Value);
                return new PasswordAuthenticationMethod(username, password.Value);
            case SshAuthMaterial.PrivateKey privateKey:
                Register(privateKey.KeyData);
                Register(privateKey.Passphrase);
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(privateKey.KeyData), writable: false);
                var keyFile = privateKey.Passphrase is null
                    ? new PrivateKeyFile(stream)
                    : new PrivateKeyFile(stream, privateKey.Passphrase);
                return new PrivateKeyAuthenticationMethod(username, keyFile);
            case SshAuthMaterial.KeyboardInteractive keyboardInteractive:
                var interactive = new KeyboardInteractiveAuthenticationMethod(username);
                interactive.AuthenticationPrompt += (_, eventArgs) =>
                {
                    if (eventArgs.Prompts.Count == 0)
                    {
                        return;
                    }

                    var prompts = eventArgs.Prompts
                        .Select(prompt => new SshAuthenticationPrompt(prompt.Request, !prompt.IsEchoed))
                        .ToArray();
                    var responses = keyboardInteractive.RespondAsync(prompts, cancellationToken)
                        .AsTask().GetAwaiter().GetResult();
                    for (var index = 0; index < eventArgs.Prompts.Count && index < responses.Count; index++)
                    {
                        eventArgs.Prompts[index].Response = responses[index];
                        Register(responses[index]);
                    }
                };
                return interactive;
            case SshAuthMaterial.Agent:
                throw new NotSupportedException("SSH.NET does not expose the platform SSH agent through its public API.");
            default:
                throw new ArgumentOutOfRangeException(nameof(authentication));
        }
    }

    private void VerifyHostKey(
        SshConnectRequest request,
        VerificationState verification,
        HostKeyEventArgs eventArgs,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = _hostKeyVerifier.VerifyAsync(new HostKeyVerificationRequest(
                request.Host,
                request.Port,
                new HostKeyInfo(eventArgs.HostKeyName, eventArgs.HostKey, eventArgs.FingerPrintSHA256),
                request.HostKeyPolicy), cancellationToken).GetAwaiter().GetResult();
            eventArgs.CanTrust = result.IsSuccess;
            if (result.IsFailure)
            {
                verification.Failure = result.Failure;
            }
        }
        catch (OperationCanceledException)
        {
            eventArgs.CanTrust = false;
            verification.Failure = new SshFailure(SshError.Cancelled, SshErrorMessages.ToUserMessage(SshError.Cancelled));
        }
    }

    private void Register(string? secret)
    {
        if (secret is { Length: >= 4 })
        {
            _secretRegistry?.Register(secret);
        }
    }

    private static void Validate(SshConnectRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Port, 65_535);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxAuthenticationAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.OperationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.KeepAliveInterval, TimeSpan.Zero);
    }

    private sealed class VerificationState
    {
        public SshFailure? Failure { get; set; }
    }
}
