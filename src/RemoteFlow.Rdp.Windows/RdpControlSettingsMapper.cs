using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Rdp.Windows;

internal static class RdpControlSettingsMapper
{
    private const uint _requiredAuthenticationLevel = 2;

    /// <summary>Maps a connection to native-control values without activating or accessing COM.</summary>
    /// <param name="connection">The stored RDP connection to map.</param>
    /// <param name="viewportWidth">The available embedded viewport width in physical pixels.</param>
    /// <param name="viewportHeight">The available embedded viewport height in physical pixels.</param>
    /// <param name="displayScaling">The Avalonia render scaling for the viewport.</param>
    public static RdpControlSettings Map(
        Connection connection,
        int viewportWidth,
        int viewportHeight,
        double displayScaling)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewportHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(displayScaling);

        var options = connection.Rdp;
        var hasStoredResolution = options.Width is > 0 && options.Height is > 0;
        var scaleFactor = MapScaleFactor(displayScaling);

        return new RdpControlSettings(
            connection.Host,
            connection.Port,
            connection.Username,
            options.Domain,
            hasStoredResolution ? options.Width!.Value : viewportWidth,
            hasStoredResolution ? options.Height!.Value : viewportHeight,
            ColorDepth: 32,
            new RdpControlAdvancedSettings(
                options.RedirectClipboard,
                options.RedirectDrives,
                AuthenticationLevel: _requiredAuthenticationLevel,
                EnableCredSspSupport: true,
                SmartSizing: false,
                KeyboardHookMode: RdpKeyboardHookMode.OnRemoteComputer),
            DesktopScaleFactor: scaleFactor,
            DeviceScaleFactor: scaleFactor,
            new IgnoredExternalRdpDisplayOptions(
                FullScreenRequested: options.FullScreen,
                MultiMonitorRequested: options.Multimon));
    }

    internal static uint MapScaleFactor(double displayScaling)
    {
        // The ActiveX control accepts only 100, 140, and 180. Nearest-value boundaries are 120% and
        // 160%; an exact tie stays on the lower factor to avoid making remote text unexpectedly larger.
        var requested = displayScaling * 100d;
        ReadOnlySpan<uint> supported = [100u, 140u, 180u];
        var nearest = supported[0];
        var nearestDistance = Math.Abs(requested - nearest);

        foreach (var candidate in supported[1..])
        {
            var distance = Math.Abs(requested - candidate);
            if (distance < nearestDistance)
            {
                nearest = candidate;
                nearestDistance = distance;
            }
        }

        return nearest;
    }
}
