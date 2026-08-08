namespace RemoteFlow.Application.Abstractions.Ssh;

public static class SshErrorMessages
{
    public static string ToUserMessage(SshError error)
    {
        return error switch
        {
            SshError.DnsFailure => "The host name could not be resolved. Check the spelling, DNS, and your network connection.",
            SshError.ConnectionRefused => "The server refused the connection. Verify the host, SSH port, and that the SSH service is running.",
            SshError.Timeout => "The SSH operation timed out. Check network reachability or increase the configured timeout.",
            SshError.AuthFailed => "The server rejected the credentials. Retry and update the password, key, passphrase, or agent selection.",
            SshError.HostKeyUnknown => "The server identity is not trusted. Review and verify its host-key fingerprint before connecting.",
            SshError.HostKeyMismatch => "The server identity changed. Stop and verify both fingerprints through a trusted channel before reconnecting.",
            SshError.HostKeyRevoked => "This server key is revoked. Remove or replace the revoked key only after verifying the server identity.",
            SshError.ChannelClosed => "The remote shell closed. Review any terminal output, then reconnect if you want a new session.",
            SshError.NetworkChanged => "The network changed or resumed from sleep. Check connectivity, then reconnect this tab manually.",
            SshError.Cancelled => "The SSH operation was cancelled. Retry when you are ready.",
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
    }
}
