using Avalonia;

namespace RemoteFlow.TerminalSpike;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        App.LaunchOptions = SpikeLaunchOptions.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}

internal sealed record SpikeLaunchOptions(
    string Shell,
    string[] Arguments,
    string WorkingDirectory,
    string ColorMode,
    int ReadBufferSize)
{
    public static SpikeLaunchOptions Parse(string[] args)
    {
        string? shell = null;
        string? workingDirectory = null;
        var colorMode = "truecolor";
        var readBufferSize = 4096;
        List<string> shellArguments = [];

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--shell" when index + 1 < args.Length:
                    shell = args[++index];
                    break;
                case "--arg" when index + 1 < args.Length:
                    shellArguments.Add(args[++index]);
                    break;
                case "--cwd" when index + 1 < args.Length:
                    workingDirectory = args[++index];
                    break;
                case "--color" when index + 1 < args.Length:
                    colorMode = args[++index].Equals("256", StringComparison.OrdinalIgnoreCase)
                        ? "256"
                        : "truecolor";
                    break;
                case "--read-buffer-size" when index + 1 < args.Length && int.TryParse(args[++index], out var size):
                    readBufferSize = Math.Clamp(size, 1, 1024 * 1024);
                    break;
            }
        }

        shell ??= ResolveDefaultShell();
        workingDirectory ??= Environment.CurrentDirectory;

        if (shellArguments.Count == 0 && OperatingSystem.IsWindows() &&
            Path.GetFileName(shell).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase))
        {
            shellArguments.Add("-NoLogo");
        }

        return new SpikeLaunchOptions(shell, [.. shellArguments], workingDirectory, colorMode, readBufferSize);
    }

    private static string ResolveDefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return FindOnPath("pwsh.exe")
                ?? Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        }

        return Environment.GetEnvironmentVariable("SHELL")
            ?? (File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, fileName))
            .FirstOrDefault(File.Exists);
    }
}
