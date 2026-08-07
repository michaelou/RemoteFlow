namespace RemoteFlow.Domain.Enums;

public static class ProtocolTypeExtensions
{
    public static int GetDefaultPort(this ProtocolType protocol)
    {
        return protocol switch
        {
            ProtocolType.Ssh => 22,
            ProtocolType.Sftp => 22,
            ProtocolType.Rdp => 3389,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };
    }
}
