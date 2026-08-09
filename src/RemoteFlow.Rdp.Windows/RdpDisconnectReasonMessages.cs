namespace RemoteFlow.Rdp.Windows;

internal static class RdpDisconnectReasonMessages
{
    public static string ToUserMessage(uint disconnectReason, uint extendedReason, string? nativeDescription)
    {
        return (disconnectReason, extendedReason) switch
        {
            (516, _) or (260, _) => "The RDP host could not be reached. Check the host name, port, and network connection.",
            (_, 3) => "The remote session ended because it reached its idle timeout.",
            (_, 4) => "The remote session ended because login did not complete in time.",
            (_, 5) => "The remote session was replaced by another connection or the server session limit was reached.",
            (_, 7) or (_, 9) => "The RDP server refused the connection because this account is not allowed to sign in remotely.",
            (_, 12) => "An administrator ended the remote session.",
            (_, >= 256 and <= 263) => "The RDP server could not issue or validate a Remote Desktop licence.",
            _ when !string.IsNullOrWhiteSpace(nativeDescription) &&
                !nativeDescription.Equals("An internal error has occurred.", StringComparison.OrdinalIgnoreCase) =>
                nativeDescription,
            _ => $"The RDP connection ended (reason {disconnectReason}, extended reason {extendedReason}).",
        };
    }
}
