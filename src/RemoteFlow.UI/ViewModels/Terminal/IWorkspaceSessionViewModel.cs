using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.UI.ViewModels.Terminal;

/// <summary>The protocol-neutral state needed by the shared terminal workspace.</summary>
public interface IWorkspaceSessionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    string Title { get; }

    string TabTitle { get; }

    EnvironmentKind Environment { get; }

    string AccentColorHex { get; }

    string TabBackgroundHex { get; }

    string ChromeTintHex { get; }

    string EnvironmentCue { get; }

    string ProtocolCue { get; }

    string StatusText { get; }

    string TabAccessibleName { get; }

    string CloseTabAccessibleName { get; }

    bool IsActive { get; }

    /// <summary>Whether the workspace is showing every session side by side rather than one at a time.</summary>
    bool IsTiled { get; }

    /// <summary>Whether this session's content belongs on screen: <c>IsTiled || IsActive</c>. Selection and
    /// visibility are separate in a grid, where every session is on screen but only one has the keyboard —
    /// and the accent chrome, the recovery panel and every keyboard command still follow that one.</summary>
    bool IsContentVisible { get; }

    bool IsLive { get; }

    bool IsEnded { get; }

    bool CanOpenInSystemTerminal { get; }

    string? EndedMessage { get; }

    string RecoveryActionLabel { get; }

    IAsyncRelayCommand RetryCommand { get; }

    void SetActive(bool isActive);

    void SetTiled(bool isTiled);
}

/// <summary>Optional view seam for protocol implementations owned by another platform assembly.</summary>
public interface IWorkspaceSessionContentProvider
{
    Control CreateSessionContent();
}

/// <summary>Optional focus seam for a session surface that is not an Avalonia terminal control.</summary>
public interface IWorkspaceSessionFocusTarget
{
    bool FocusSessionContent();
}

/// <summary>Lets an in-surface recovery action request that its workspace tab be closed.</summary>
public interface IWorkspaceSessionCloseRequestSource
{
    event EventHandler? CloseRequested;
}

public static class WorkspaceSessionAppearance
{
    public static string ResolveAccentColor(EnvironmentKind environment, string? colorOverrideHex)
    {
        return !string.IsNullOrWhiteSpace(colorOverrideHex) &&
            System.Text.RegularExpressions.Regex.IsMatch(colorOverrideHex, "^#[0-9A-Fa-f]{6}$")
            ? colorOverrideHex.ToUpperInvariant()
            : environment switch
            {
                EnvironmentKind.Development => "#FF7B72",
                EnvironmentKind.Staging => "#FFCA58",
                EnvironmentKind.Production => "#5DE28C",
                EnvironmentKind.Unspecified => "#7E8998",
                _ => throw new ArgumentOutOfRangeException(nameof(environment)),
            };
    }

    public static string EnvironmentCue(EnvironmentKind environment)
    {
        return environment switch
        {
            EnvironmentKind.Development => "DEV",
            EnvironmentKind.Staging => "STG",
            EnvironmentKind.Production => "PROD !",
            EnvironmentKind.Unspecified => "LOCAL",
            _ => throw new ArgumentOutOfRangeException(nameof(environment)),
        };
    }

    public static string EnvironmentDescription(EnvironmentKind environment)
    {
        return environment switch
        {
            EnvironmentKind.Development => "development",
            EnvironmentKind.Staging => "staging",
            EnvironmentKind.Production => "production",
            EnvironmentKind.Unspecified => "local",
            _ => throw new ArgumentOutOfRangeException(nameof(environment)),
        };
    }
}
