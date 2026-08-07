using System.Net.Sockets;
using RemoteFlow.Application.Abstractions.Ssh;
using Tmds.Ssh;

namespace RemoteFlow.Infrastructure.Ssh;

public sealed class TmdsSshTransport(IHostKeyVerifier hostKeyVerifier) : ISshTransport
{
    private readonly IHostKeyVerifier _hostKeyVerifier =
        hostKeyVerifier ?? throw new ArgumentNullException(nameof(hostKeyVerifier));

    public async Task<SshResult<ISshConnection>> ConnectAsync(
        SshConnectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        SshFailure? hostKeyFailure = null;
        var settings = new SshClientSettings
        {
            HostName = request.Host,
            Port = request.Port,
            UserName = request.Username,
            Credentials = [CreateCredential(request.Authentication)],
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

    private static Credential CreateCredential(SshAuthMaterial authentication)
    {
        return authentication switch
        {
            SshAuthMaterial.None => new NoCredential(),
            SshAuthMaterial.Password password => new PasswordCredential(password.Value),
            SshAuthMaterial.PrivateKey privateKey => new PrivateKeyCredential(
                privateKey.KeyData.ToCharArray(),
                privateKey.Passphrase ?? string.Empty,
                "RemoteFlow private key"),
            SshAuthMaterial.Agent => new SshAgentCredentials(),
            SshAuthMaterial.KeyboardInteractive keyboardInteractive => new PasswordCredential(
                async (_, token) =>
                {
                    var responses = await keyboardInteractive.RespondAsync(
                        [new SshAuthenticationPrompt("Password:", IsSecret: true)],
                        token).ConfigureAwait(false);
                    return responses.Count == 0 ? null : responses[0];
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(authentication)),
        };
    }

    private static void Validate(SshConnectRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Port, 65_535);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Username);
        ArgumentNullException.ThrowIfNull(request.Authentication);
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

        if (Find<TimeoutException>(exception) is not null || Find<OperationCanceledException>(exception) is not null)
        {
            return SshError.Timeout;
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
        return error switch
        {
            SshError.DnsFailure => "The SSH host name could not be resolved.",
            SshError.ConnectionRefused => "The SSH server refused the connection.",
            SshError.Timeout => "The SSH connection timed out.",
            SshError.AuthFailed => "The SSH server rejected the supplied credentials.",
            SshError.HostKeyUnknown => "The SSH host key is not trusted.",
            SshError.HostKeyMismatch => "The SSH host key does not match the trusted key.",
            SshError.HostKeyRevoked => "The SSH host key is revoked.",
            SshError.ChannelClosed => "The SSH channel is closed.",
            SshError.NetworkChanged => "The SSH connection was interrupted by a network change.",
            SshError.Cancelled => "The SSH operation was cancelled.",
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
    }
}
