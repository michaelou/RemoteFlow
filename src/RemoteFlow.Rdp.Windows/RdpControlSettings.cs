namespace RemoteFlow.Rdp.Windows;

internal enum RdpKeyboardHookMode
{
    OnLocalComputer = 0,
    OnRemoteComputer = 1,
    OnRemoteComputerInFullScreen = 2,
}

internal sealed record RdpControlAdvancedSettings(
    bool RedirectClipboard,
    bool RedirectDrives,
    uint AuthenticationLevel,
    bool EnableCredSspSupport,
    bool SmartSizing,
    RdpKeyboardHookMode KeyboardHookMode);

/// <summary>External-client display options that embedded RDP deliberately does not apply.</summary>
internal sealed record IgnoredExternalRdpDisplayOptions(
    bool FullScreenRequested,
    bool MultiMonitorRequested);

/// <summary>All pre-connect values applied to the native RDP control. Credentials are deliberately absent.</summary>
internal sealed record RdpControlSettings(
    string Server,
    int RdpPort,
    string? UserName,
    string? Domain,
    int DesktopWidth,
    int DesktopHeight,
    int ColorDepth,
    RdpControlAdvancedSettings AdvancedSettings,
    uint DesktopScaleFactor,
    uint DeviceScaleFactor,
    IgnoredExternalRdpDisplayOptions IgnoredExternalDisplayOptions);
