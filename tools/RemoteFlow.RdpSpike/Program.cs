using System.Globalization;
using Avalonia;

namespace RemoteFlow.RdpSpike;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.LaunchOptions = SpikeOptions.Parse(args);
        _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}

/// <summary>How the spike was launched.
///
/// There is deliberately no password option. A password on the command line is readable by every process
/// running as the same user, which is the exposure ADR-0015 exists to avoid; the spike lets the control
/// put up its own credential prompt instead.</summary>
internal sealed record SpikeOptions(
    string Host,
    int Port,
    string UserName,
    string Domain,
    string Container,
    int? PinnedGeneration,
    bool SmartSizing,
    bool CredSsp,
    bool PromptForCredentials,
    int Width,
    int Height,
    bool Auto,
    bool ExitWhenDone)
{
    public bool UseWinFormsContainer =>
        Container.Equals("axhost", StringComparison.OrdinalIgnoreCase);

    public static SpikeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var host = string.Empty;
        var port = 3389;
        var user = string.Empty;
        var domain = string.Empty;
        var container = "ole";
        int? generation = null;
        var smartSizing = false;
        var credSsp = true;
        var prompt = true;
        var width = 1280;
        var height = 800;
        var auto = false;
        var exitWhenDone = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--host" when index + 1 < args.Length:
                    host = args[++index];
                    break;
                case "--port" when index + 1 < args.Length && int.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var parsedPort):
                    port = parsedPort;
                    index++;
                    break;
                case "--user" when index + 1 < args.Length:
                    user = args[++index];
                    break;
                case "--domain" when index + 1 < args.Length:
                    domain = args[++index];
                    break;
                case "--container" when index + 1 < args.Length:
                    container = args[++index];
                    break;
                case "--class" when index + 1 < args.Length && int.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var parsedClass):
                    generation = parsedClass;
                    index++;
                    break;
                case "--smart-sizing":
                    smartSizing = true;
                    break;
                case "--no-credssp":
                    credSsp = false;
                    break;
                case "--no-prompt":
                    prompt = false;
                    break;
                case "--size" when index + 1 < args.Length:
                    (width, height) = ParseSize(args[++index], width, height);
                    break;
                case "--auto":
                    auto = true;
                    break;
                case "--exit-when-done":
                    exitWhenDone = true;
                    break;
                default:
                    break;
            }
        }

        return new SpikeOptions(
            host,
            port,
            user,
            domain,
            container,
            generation,
            smartSizing,
            credSsp,
            prompt,
            width,
            height,
            auto,
            exitWhenDone);
    }

    private static (int Width, int Height) ParseSize(string value, int fallbackWidth, int fallbackHeight)
    {
        var parts = value.Split('x', 'X');
        return parts.Length == 2 &&
            int.TryParse(parts[0], CultureInfo.InvariantCulture, out var width) &&
            int.TryParse(parts[1], CultureInfo.InvariantCulture, out var height)
            ? (width, height)
            : (fallbackWidth, fallbackHeight);
    }
}
