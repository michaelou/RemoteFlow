using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Platform;

public sealed class SystemPlatform : ISystemPlatform
{
    public OperatingSystemFamily OperatingSystem =>
        System.OperatingSystem.IsWindows() ? OperatingSystemFamily.Windows :
        System.OperatingSystem.IsMacOS() ? OperatingSystemFamily.MacOs :
        OperatingSystemFamily.Linux;

    public string CurrentDirectory => Environment.CurrentDirectory;

    public string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string? GetEnvironmentVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }

    public string? FindExecutable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(name) ? Path.GetFullPath(name) : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        var extensions = System.OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];
        foreach (var directory in (path ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions.Prepend(string.Empty).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(directory, name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (System.OperatingSystem.IsWindows())
        {
            var system = Environment.SystemDirectory;
            var candidates = new[]
            {
                Path.Combine(system, name),
                Path.Combine(system, "WindowsPowerShell", "v1.0", name),
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        return null;
    }

    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public string? GetLoginShellFromPasswd()
    {
        if (System.OperatingSystem.IsWindows() || !File.Exists("/etc/passwd"))
        {
            return null;
        }

        var userName = Environment.GetEnvironmentVariable("USER") ?? Environment.UserName;
        foreach (var line in File.ReadLines("/etc/passwd"))
        {
            var fields = line.Split(':');
            if (fields.Length >= 7 && string.Equals(fields[0], userName, StringComparison.Ordinal))
            {
                return fields[^1].Trim();
            }
        }

        return null;
    }
}
