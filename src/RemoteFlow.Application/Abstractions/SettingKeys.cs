using RemoteFlow.Application.Abstractions.Backup;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.Application.Abstractions;

public interface ISettingKey
{
    string Name { get; }

    Type ValueType { get; }

    object? UntypedDefaultValue { get; }
}

public sealed record SettingKey<T> : ISettingKey
{
    public SettingKey(string name, T defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        DefaultValue = defaultValue;
    }

    public string Name { get; }

    public T DefaultValue { get; }

    public Type ValueType => typeof(T);

    public object? UntypedDefaultValue => DefaultValue;
}

public enum AppTheme
{
    Light = 1,
    Dark = 2,
    System = 3,
}

public enum TerminalCursorStyle
{
    Block = 1,
    Underline = 2,
    Bar = 3,
}

public enum TerminalBellMode
{
    None = 0,
    Audible = 1,
    Visual = 2,
}

public enum CtrlCPolicy
{
    SigintAlways = 1,
    CopyWhenSelected = 2,
}

public enum RemoteEditConflictDefault
{
    Prompt = 1,
    Overwrite = 2,
    KeepBoth = 3,
    Discard = 4,
}

/// <summary>What a chunked object transfer does when the destination already exists. Defaults to
/// <see cref="Overwrite"/>: an object store has no atomic publish to fall back on, and re-uploading is the
/// only way to make the destination match the source.</summary>
public enum StorageConflictDefault
{
    Prompt = 1,
    Overwrite = 2,
    Skip = 3,
}

public enum SshTransport
{
    Tmds = 1,
    SshNet = 2,
}

public enum WindowsRdpOpenMode
{
    Embedded = 1,
    External = 2,
}

public enum WorkspaceLayoutMode
{
    Tabs = 1,
    Grid = 2,
}

public static class SettingKeys
{
    public static SettingKey<AppTheme> Theme { get; } = new("Theme", AppTheme.Dark);
    public static SettingKey<string?> AccentColor { get; } = new("AccentColor", null);
    public static SettingKey<string?> TerminalFontFamily { get; } = new("TerminalFontFamily", null);
    public static SettingKey<int> TerminalFontSize { get; } = new("TerminalFontSize", 13);
    public static SettingKey<int> TerminalScrollback { get; } = new("TerminalScrollback", 10_000);
    public static SettingKey<string?> TerminalColorScheme { get; } = new("TerminalColorScheme", null);
    public static SettingKey<TerminalCursorStyle> CursorStyle { get; } = new("CursorStyle", TerminalCursorStyle.Block);
    public static SettingKey<bool> CursorBlink { get; } = new("CursorBlink", true);
    public static SettingKey<TerminalBellMode> BellMode { get; } = new("BellMode", TerminalBellMode.None);
    public static SettingKey<bool> ReflowOnResize { get; } = new("ReflowOnResize", false);
    public static SettingKey<bool> CopyOnSelect { get; } = new("CopyOnSelect", false);
    public static SettingKey<bool> SuppressPasteWarning { get; } = new("SuppressPasteWarning", false);
    public static SettingKey<CtrlCPolicy> CtrlCPolicy { get; } =
        new("CtrlCPolicy", global::RemoteFlow.Application.Abstractions.CtrlCPolicy.SigintAlways);
    public static SettingKey<string> KeymapProfile { get; } = new("KeymapProfile", "auto");
    public static SettingKey<bool> ConfirmCloseActiveSession { get; } = new("ConfirmCloseActiveSession", true);
    public static SettingKey<string?> DefaultShell { get; } = new("DefaultShell", null);
    public static SettingKey<global::RemoteFlow.Application.Services.ShellProfile[]> ShellProfiles { get; } = new("ShellProfiles", []);
    public static SettingKey<string?> DefaultShellProfileId { get; } = new("DefaultShellProfileId", null);
    public static SettingKey<string?> SystemTerminalCommand { get; } = new("SystemTerminalCommand", null);
    public static SettingKey<string?> SftpDownloadDir { get; } = new("SftpDownloadDir", null);
    public static SettingKey<string?> RemoteEditTempDir { get; } = new("RemoteEditTempDir", null);
    public static SettingKey<RemoteEditConflictDefault> RemoteEditConflictDefault { get; } =
        new("RemoteEditConflictDefault", global::RemoteFlow.Application.Abstractions.RemoteEditConflictDefault.Prompt);
    public static SettingKey<HostKeyPolicy> DefaultHostKeyPolicy { get; } =
        new("DefaultHostKeyPolicy", HostKeyPolicy.TrustOnFirstUse);
    public static SettingKey<SshTransport> SshTransport { get; } =
        new("SshTransport", global::RemoteFlow.Application.Abstractions.SshTransport.Tmds);
    public static SettingKey<WindowsRdpOpenMode> WindowsRdpOpenMode { get; } =
        new("WindowsRdpOpenMode", global::RemoteFlow.Application.Abstractions.WindowsRdpOpenMode.Embedded);
    public static SettingKey<WorkspaceLayoutMode> WorkspaceLayout { get; } =
        new("WorkspaceLayout", WorkspaceLayoutMode.Tabs);
    public static SettingKey<int> WorkspaceGridMaxColumns { get; } = new("WorkspaceGridMaxColumns", 3);
    public static SettingKey<int> RecentLimit { get; } = new("RecentLimit", 20);
    public static SettingKey<string?> WindowLayout { get; } = new("WindowLayout", null);
    public static SettingKey<int> SchemaVersion { get; } = new("SchemaVersion", 1);
    public static SettingKey<bool> ForceFileVault { get; } = new("ForceFileVault", false);
    public static SettingKey<bool> CheckForUpdates { get; } = new("CheckForUpdates", false);
    public static SettingKey<string?> StorageDownloadDir { get; } = new("StorageDownloadDir", null);
    public static SettingKey<int> StorageMaxPartsInFlight { get; } = new("StorageMaxPartsInFlight", 4);
    public static SettingKey<StorageConflictDefault> StorageConflictDefault { get; } =
        new("StorageConflictDefault", global::RemoteFlow.Application.Abstractions.StorageConflictDefault.Overwrite);

    /// <summary>Whether the connection explorer draws the host and port under each connection's name. A
    /// display preference, remembered because it is set once and then lived with.</summary>
    public static SettingKey<bool> ShowConnectionDetailLine { get; } = new("ShowConnectionDetailLine", true);

    /// <summary>How automatic backup is configured: whether it runs, how many archives to keep, and where
    /// they go. One key rather than several so the runner can never read a half-changed configuration.
    /// Note that settings travel inside backup archives, so importing one can point this machine at a
    /// destination that does not exist here — the runner re-validates on every run and reports rather than
    /// throws, and a passphrase it cannot find blocks the run outright.</summary>
    public static SettingKey<AutoBackupOptions> AutoBackup { get; } = new("AutoBackup", AutoBackupOptions.Disabled);

    /// <summary>Where a local browser pane was last pointed. Shared by the Storage and SFTP pages, so the
    /// two land in the same folder rather than each keeping its own idea of where you were.</summary>
    public static SettingKey<string?> LastLocalFolder { get; } = new("LastLocalFolder", null);

    public static IReadOnlyList<ISettingKey> All { get; } =
    [
        Theme,
        AccentColor,
        TerminalFontFamily,
        TerminalFontSize,
        TerminalScrollback,
        TerminalColorScheme,
        CursorStyle,
        CursorBlink,
        BellMode,
        ReflowOnResize,
        CopyOnSelect,
        SuppressPasteWarning,
        CtrlCPolicy,
        KeymapProfile,
        ConfirmCloseActiveSession,
        DefaultShell,
        ShellProfiles,
        DefaultShellProfileId,
        SystemTerminalCommand,
        SftpDownloadDir,
        RemoteEditTempDir,
        RemoteEditConflictDefault,
        DefaultHostKeyPolicy,
        SshTransport,
        .. OperatingSystem.IsWindows() ? new ISettingKey[] { WindowsRdpOpenMode } : [],
        WorkspaceLayout,
        WorkspaceGridMaxColumns,
        RecentLimit,
        WindowLayout,
        SchemaVersion,
        ForceFileVault,
        CheckForUpdates,
        StorageDownloadDir,
        StorageMaxPartsInFlight,
        StorageConflictDefault,
        LastLocalFolder,
        ShowConnectionDetailLine,
        AutoBackup,
    ];
}
