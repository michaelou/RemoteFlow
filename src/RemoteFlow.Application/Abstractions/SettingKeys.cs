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
    ];
}
