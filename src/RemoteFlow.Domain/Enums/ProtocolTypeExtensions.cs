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
            ProtocolType.S3 => 443,
            ProtocolType.AzureBlob => 443,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };
    }

    /// <summary>Whether the protocol reaches a cloud object store rather than a host running a server.
    /// The branch sites that behave differently for S3 and Azure Blob ask this instead of each hand-listing
    /// both members, so adding a third object store touches one place.</summary>
    public static bool IsObjectStorage(this ProtocolType protocol)
    {
        return protocol is ProtocolType.S3 or ProtocolType.AzureBlob;
    }

    /// <summary>What to put in front of a person. <c>ToString</c> gives "AzureBlob", and upper-casing it
    /// gives "AZUREBLOB"; neither is a name anybody uses for the product.</summary>
    public static string GetDisplayName(this ProtocolType protocol)
    {
        return protocol switch
        {
            ProtocolType.Ssh => "SSH",
            ProtocolType.Sftp => "SFTP",
            ProtocolType.Rdp => "RDP",
            ProtocolType.S3 => "S3",
            ProtocolType.AzureBlob => "Azure Blob",
            _ => throw new ArgumentOutOfRangeException(nameof(protocol)),
        };
    }
}
