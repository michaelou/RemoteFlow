using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Infrastructure.Ssh.Auth;
using Tmds.Ssh;

namespace RemoteFlow.Infrastructure.Ssh;

public sealed class TmdsSshTransport(
    IHostKeyVerifier hostKeyVerifier,
    ILogger<TmdsSshTransport>? logger = null,
    ISshAgentDiscovery? agentDiscovery = null,
    ISecretRegistry? secretRegistry = null) : ISshTransport
{
    private readonly IHostKeyVerifier _hostKeyVerifier =
        hostKeyVerifier ?? throw new ArgumentNullException(nameof(hostKeyVerifier));
    private readonly ILogger<TmdsSshTransport>? _logger = logger;
    private readonly ISshAgentDiscovery? _agentDiscovery = agentDiscovery;
    private readonly ISecretRegistry? _secretRegistry = secretRegistry;

    public async Task<SshResult<ISshConnection>> ConnectAsync(
        SshConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        SshFailure? hostKeyFailure = null;
        var authenticationMethods = request.AuthenticationMethods.Count > 0
            ? request.AuthenticationMethods
            : [request.Authentication];
        if (authenticationMethods.Any(method => method is SshAuthMaterial.Agent) &&
            (_agentDiscovery?.Discover().Any(endpoint => endpoint.IsAvailable) == false))
        {
            _logger?.LogInformation(
                "No SSH agent endpoint is available; authentication will continue with the next configured method.");
        }

        var settings = new SshClientSettings
        {
            HostName = request.Host,
            Port = request.Port,
            UserName = request.Username,
            Credentials = [.. authenticationMethods.Select(method => CreateCredential(method, request.MaxAuthenticationAttempts))],
            ConnectTimeout = request.ConnectTimeout,
            KeepAliveInterval = request.KeepAliveInterval,
            AutoConnect = false,
            AutoReconnect = false,
            UserKnownHostsFilePaths = [],
            GlobalKnownHostsFilePaths = [],
            UpdateKnownHostsFileAfterAuthentication = false,
            HostAuthentication = async (context, token) =>
            {
                var connectionInfo = context.ConnectionInfo;
                var publicKeyText = connectionInfo.ServerKey.Key.ToString();
                var fields = publicKeyText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2)
                {
                    hostKeyFailure = new SshFailure(
                        SshError.HostKeyUnknown,
                        "The SSH server returned an invalid public host key.");
                    return false;
                }

                var publicKey = Convert.FromBase64String(fields[1]);
                var result = await _hostKeyVerifier.VerifyAsync(new HostKeyVerificationRequest(
                    request.Host,
                    request.Port,
                    new HostKeyInfo(fields[0], publicKey, connectionInfo.ServerKey.Key.SHA256FingerPrint),
                    request.HostKeyPolicy), token).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    hostKeyFailure = result.Failure;
                    return false;
                }

                return true;
            },
        };
        var client = new SshClient(settings);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return SshResult<ISshConnection>.Success(new TmdsSshConnection(client, request.OperationTimeout));
        }
        catch (Exception exception)
        {
            client.Dispose();
            return hostKeyFailure is not null
                ? SshResult<ISshConnection>.Fail(hostKeyFailure.Error, hostKeyFailure.Message)
                : SshErrorMapper.Failure<ISshConnection>(exception, cancellationToken);
        }
    }

    private Credential CreateCredential(SshAuthMaterial authentication, int maxAttempts)
    {
        return authentication switch
        {
            SshAuthMaterial.None => new NoCredential(),
            SshAuthMaterial.Password password => CreatePasswordCredential(password),
            SshAuthMaterial.PrivateKey privateKey => CreatePrivateKeyCredential(privateKey),
            SshAuthMaterial.Agent => new SshAgentCredentials(),
            SshAuthMaterial.KeyboardInteractive keyboardInteractive => new PasswordCredential(
                async (context, token) =>
                {
                    if (context.Attempt > maxAttempts)
                    {
                        return null;
                    }

                    var responses = await keyboardInteractive.RespondAsync(
                        [new SshAuthenticationPrompt("Password:", IsSecret: true)],
                        token).ConfigureAwait(false);
                    foreach (var response in responses.Where(response => response.Length >= 4))
                    {
                        _secretRegistry?.Register(response);
                    }
                    return responses.Count == 0 ? null : responses[0];
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(authentication)),
        };
    }

    private PasswordCredential CreatePasswordCredential(SshAuthMaterial.Password password)
    {
        if (password.Value.Length >= 4)
        {
            _secretRegistry?.Register(password.Value);
        }
        return new PasswordCredential(password.Value);
    }

    private PrivateKeyCredential CreatePrivateKeyCredential(SshAuthMaterial.PrivateKey privateKey)
    {
        if (privateKey.KeyData.Length >= 4)
        {
            _secretRegistry?.Register(privateKey.KeyData);
        }
        if (privateKey.Passphrase is { Length: >= 4 } passphrase)
        {
            _secretRegistry?.Register(passphrase);
        }
        return new PrivateKeyCredential(
            privateKey.KeyData.ToCharArray(),
            privateKey.Passphrase ?? string.Empty,
            "RemoteFlow private key");
    }

    private static void Validate(SshConnectRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Port, 65_535);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentNullException.ThrowIfNull(request.Authentication);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxAuthenticationAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(request.OperationTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.KeepAliveInterval, TimeSpan.Zero);
    }
}

internal static class SshErrorMapper
{
    public static SshResult<T> Failure<T>(Exception exception, CancellationToken cancellationToken)
    {
        var error = Classify(exception, cancellationToken);
        return SshResult<T>.Fail(error, Message(error));
    }

    private static SshError Classify(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return SshError.Cancelled;
        }

        if (Find<TimeoutException>(exception) is not null ||
            Find<OperationCanceledException>(exception) is not null ||
            Find<Renci.SshNet.Common.SshOperationTimeoutException>(exception) is not null)
        {
            return SshError.Timeout;
        }

        if (Find<Renci.SshNet.Common.SshAuthenticationException>(exception) is not null)
        {
            return SshError.AuthFailed;
        }

        if (Find<SshChannelClosedException>(exception) is not null ||
            Find<SshChannelException>(exception) is not null)
        {
            return SshError.ChannelClosed;
        }

        var socketException = Find<SocketException>(exception);
        return socketException is not null
            ? Map(socketException.SocketErrorCode)
            : exception.ToString().Contains("auth", StringComparison.OrdinalIgnoreCase) ||
            exception.ToString().Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            ? SshError.AuthFailed
            : SshError.NetworkChanged;
    }

#pragma warning disable IDE0072 // Unlisted socket errors intentionally normalize to NetworkChanged.
    private static SshError Map(SocketError error)
    {
        return error switch
        {
            SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData => SshError.DnsFailure,
            SocketError.ConnectionRefused => SshError.ConnectionRefused,
            SocketError.TimedOut => SshError.Timeout,
            SocketError.NetworkDown or SocketError.NetworkReset or SocketError.NetworkUnreachable or
                SocketError.HostDown or SocketError.HostUnreachable or SocketError.ConnectionReset =>
                SshError.NetworkChanged,
            _ => SshError.NetworkChanged,
        };
    }
#pragma warning restore IDE0072

    private static TException? Find<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = (Exception?)exception; current is not null; current = current.InnerException)
        {
            if (current is TException match)
            {
                return match;
            }
        }

        return null;
    }

    private static string Message(SshError error)
    {
        return SshErrorMessages.ToUserMessage(error);
    }
}
