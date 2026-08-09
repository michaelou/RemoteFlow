using System.Globalization;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Infrastructure.Platform.Rdp;

/// <summary>Renders a connection as the `.rdp` text that `mstsc.exe` reads.</summary>
/// <remarks>
/// There is deliberately no `password 51:b:` line and no code path that could add one. That field holds a
/// DPAPI blob tied to the user profile: it is not plaintext, but it is still a credential sitting in a
/// file, and a file that leaks is a credential that leaks. The password reaches the client through
/// <c>cmdkey</c> instead, and only for as long as the launch takes.
/// </remarks>
internal static class RdpFileBuilder
{
    public static string Build(Connection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var options = connection.Rdp;
        var lines = new List<string>
        {
            $"full address:s:{connection.Host}:{Number(connection.Port)}",
        };

        if (!string.IsNullOrWhiteSpace(connection.Username))
        {
            lines.Add($"username:s:{connection.Username}");
        }

        if (!string.IsNullOrWhiteSpace(options.Domain))
        {
            lines.Add($"domain:s:{options.Domain}");
        }

        lines.Add($"screen mode id:i:{Number(options.FullScreen ? 2 : 1)}");
        if (options.Width is { } width && options.Height is { } height)
        {
            lines.Add($"desktopwidth:i:{Number(width)}");
            lines.Add($"desktopheight:i:{Number(height)}");
        }

        lines.Add($"use multimon:i:{Number(options.Multimon ? 1 : 0)}");
        lines.Add($"redirectclipboard:i:{Number(options.RedirectClipboard ? 1 : 0)}");
        lines.Add($"drivestoredirect:s:{(options.RedirectDrives ? "*" : string.Empty)}");
        lines.Add("audiomode:i:0");
        lines.Add("authentication level:i:2");
        lines.Add("prompt for credentials:i:0");
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static string Number(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
