using RemoteFlow.Application.Abstractions.Sftp;

namespace RemoteFlow.UI.ViewModels.Sftp;

public sealed record RemoteEditSnapshotViewModel(string Size, string Modified, string Hash)
{
    public static RemoteEditSnapshotViewModel From(RemoteSnapshot snapshot)
    {
        return snapshot.Exists
            ? new RemoteEditSnapshotViewModel(
                FormatSize(snapshot.Size),
                snapshot.MTimeUtc.ToLocalTime().ToString("G", System.Globalization.CultureInfo.CurrentCulture),
                snapshot.Sha256 ?? "Not calculated (file exceeds 8 MiB)")
            : new RemoteEditSnapshotViewModel("Missing", "—", "—");
    }

    private static string FormatSize(long bytes)
    {
        return bytes == 1 ? "1 byte" : $"{bytes:N0} bytes";
    }
}

public sealed class RemoteEditConflictDialogViewModel(RemoteEditConflict conflict)
{
    public string RemotePath { get; } = conflict.RemotePath;

    public RemoteEditSnapshotViewModel Downloaded { get; } =
        RemoteEditSnapshotViewModel.From(conflict.DownloadedSnapshot);

    public RemoteEditSnapshotViewModel Current { get; } =
        RemoteEditSnapshotViewModel.From(conflict.CurrentSnapshot);
}
