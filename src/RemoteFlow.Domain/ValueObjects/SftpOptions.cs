namespace RemoteFlow.Domain.ValueObjects;

public sealed class SftpOptions
{
    private SftpOptions()
    {
    }

    public string? RemoteRootPath { get; private set; }

    public string? LocalDownloadPath { get; private set; }

    public bool PreserveTimestamps { get; private set; }

    public bool ShowHiddenFiles { get; private set; }

    public static SftpOptions Default()
    {
        return new();
    }

    public SftpOptions Configure(
        string? remoteRootPath = null,
        string? localDownloadPath = null,
        bool preserveTimestamps = false,
        bool showHiddenFiles = false)
    {
        RemoteRootPath = string.IsNullOrWhiteSpace(remoteRootPath) ? null : remoteRootPath.Trim();
        LocalDownloadPath = string.IsNullOrWhiteSpace(localDownloadPath) ? null : localDownloadPath.Trim();
        PreserveTimestamps = preserveTimestamps;
        ShowHiddenFiles = showHiddenFiles;
        return this;
    }
}
