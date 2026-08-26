using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Application.Services;

public sealed record ShellProfile
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required string ShellPath { get; init; }

    public string[] Arguments { get; init; } = [];

    public required string WorkingDirectory { get; init; }

    public Dictionary<string, string> EnvironmentVariables { get; init; } = new(StringComparer.Ordinal);

    public string Icon { get; init; } = "terminal";
}

public interface IShellProfileService
{
    event EventHandler? ProfilesChanged;

    Task<IReadOnlyList<ShellProfile>> GetProfilesAsync(CancellationToken cancellationToken = default);

    Task<ShellProfile> GetDefaultProfileAsync(CancellationToken cancellationToken = default);

    Task SaveProfilesAsync(
        IReadOnlyList<ShellProfile> profiles,
        string defaultProfileId,
        CancellationToken cancellationToken = default);

    PtySpawnOptions CreateSpawnOptions(ShellProfile profile);
}

public sealed class ShellProfileService(ISettingsStore settings, ISystemPlatform platform) : IShellProfileService, IDisposable
{
    private static readonly string[] _posixFallbackShells = ["/bin/bash", "/bin/zsh", "/bin/sh"];
    private readonly ISettingsStore _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ISystemPlatform _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    private readonly SemaphoreSlim _initializeGate = new(1, 1);

    public event EventHandler? ProfilesChanged;

    public async Task<IReadOnlyList<ShellProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _settings.Get(SettingKeys.ShellProfiles, cancellationToken).ConfigureAwait(false);
        if (profiles.Length > 0)
        {
            return profiles;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            profiles = await _settings.Get(SettingKeys.ShellProfiles, cancellationToken).ConfigureAwait(false);
            if (profiles.Length == 0)
            {
                var detected = DetectDefaultProfile();
                profiles = [detected];
                await _settings.Set(SettingKeys.ShellProfiles, profiles, cancellationToken).ConfigureAwait(false);
                await _settings.Set(SettingKeys.DefaultShellProfileId, detected.Id, cancellationToken).ConfigureAwait(false);
            }

            return profiles;
        }
        finally
        {
            _ = _initializeGate.Release();
        }
    }

    public async Task<ShellProfile> GetDefaultProfileAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await GetProfilesAsync(cancellationToken).ConfigureAwait(false);
        var defaultId = await _settings.Get(SettingKeys.DefaultShellProfileId, cancellationToken).ConfigureAwait(false);
        return profiles.FirstOrDefault(profile => string.Equals(profile.Id, defaultId, StringComparison.Ordinal)) ?? profiles[0];
    }

    public async Task SaveProfilesAsync(
        IReadOnlyList<ShellProfile> profiles,
        string defaultProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultProfileId);
        if (profiles.Count == 0)
        {
            throw new ArgumentException("At least one shell profile is required.", nameof(profiles));
        }

        var normalized = profiles.Select(ValidateAndNormalize).ToArray();
        if (normalized.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count() != normalized.Length)
        {
            throw new ArgumentException("Shell profile IDs must be unique.", nameof(profiles));
        }

        if (!normalized.Any(profile => string.Equals(profile.Id, defaultProfileId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The default shell profile must be present in the profile list.", nameof(defaultProfileId));
        }

        await _settings.Set(SettingKeys.ShellProfiles, normalized, cancellationToken).ConfigureAwait(false);
        await _settings.Set(SettingKeys.DefaultShellProfileId, defaultProfileId, cancellationToken).ConfigureAwait(false);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public PtySpawnOptions CreateSpawnOptions(ShellProfile profile)
    {
        var normalized = ValidateAndNormalize(profile);
        if (_platform.FindExecutable(normalized.ShellPath) is null && !_platform.FileExists(normalized.ShellPath))
        {
            throw new FileNotFoundException(
                $"Shell profile '{normalized.DisplayName}' cannot start because '{normalized.ShellPath}' was not found. Update the executable in Settings > Terminal.",
                normalized.ShellPath);
        }

        var environment = new Dictionary<string, string>(normalized.EnvironmentVariables, StringComparer.Ordinal)
        {
            ["TERM"] = normalized.EnvironmentVariables.GetValueOrDefault("TERM", "xterm-256color"),
            ["COLORTERM"] = normalized.EnvironmentVariables.GetValueOrDefault("COLORTERM", "truecolor"),
        };
        return new PtySpawnOptions
        {
            ShellPath = normalized.ShellPath,
            Arguments = normalized.Arguments,
            WorkingDirectory = normalized.WorkingDirectory,
            EnvironmentVariables = environment,
        };
    }

    private ShellProfile DetectDefaultProfile()
    {
        var shell = _platform.OperatingSystem == OperatingSystemFamily.Windows
            ? DetectWindowsShell()
            : DetectPosixShell();
        var displayName = Path.GetFileNameWithoutExtension(shell);
        return new ShellProfile
        {
            Id = "default",
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Default shell" : displayName,
            ShellPath = shell,
            Arguments = DefaultArguments(shell),
            WorkingDirectory = _platform.CurrentDirectory,
            EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private string DetectWindowsShell()
    {
        return _platform.FindExecutable("pwsh.exe")
            ?? _platform.FindExecutable("powershell.exe")
            ?? ResolveComSpec()
            ?? throw new FileNotFoundException("No working shell was found. Install PowerShell 7 or enable Windows PowerShell/cmd.exe.");
    }

    private string DetectPosixShell()
    {
#pragma warning disable IDE0046 // Sequential probes make the documented precedence explicit.
        var environmentShell = _platform.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(environmentShell) && _platform.FileExists(environmentShell))
        {
            return environmentShell;
        }

        var passwdShell = _platform.GetLoginShellFromPasswd();
        if (!string.IsNullOrWhiteSpace(passwdShell) && _platform.FileExists(passwdShell))
        {
            return passwdShell;
        }

        return _posixFallbackShells.FirstOrDefault(_platform.FileExists)
            ?? throw new FileNotFoundException("No working POSIX shell was found. Install bash, zsh, or a compatible /bin/sh.");
#pragma warning restore IDE0046
    }

    private string? ResolveComSpec()
    {
        var comSpec = _platform.GetEnvironmentVariable("ComSpec");
        return !string.IsNullOrWhiteSpace(comSpec) && _platform.FileExists(comSpec)
            ? comSpec
            : _platform.FindExecutable("cmd.exe");
    }

    /// <summary>
    /// What the detected shell is started with.
    /// </summary>
    /// <remarks>
    /// bash is given nothing. On a PTY it is interactive and reads the user's <c>~/.bashrc</c>, which is where
    /// the prompt, the aliases and <c>dircolors</c> live — the same shell a terminal emulator gives you.
    /// <c>--noprofile --norc</c> suppressed all of it, so on Linux the terminal opened on a bare
    /// <c>bash-5.3$</c> with no colour and misaligned <c>ls</c> output, while PowerShell on Windows kept its
    /// profile and looked right. Only Windows shells need arguments: cmd.exe to stay open and quiet, and
    /// PowerShell to skip its banner and stay open.
    /// </remarks>
    private static string[] DefaultArguments(string shell)
    {
        var fileName = Path.GetFileName(shell);
        return fileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            ? ["/Q", "/D", "/K"]
            : fileName.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
                ? ["-NoLogo", "-NoExit"]
                : [];
    }

    private static ShellProfile ValidateAndNormalize(ShellProfile profile)
    {
#pragma warning disable IDE0046 // The validation exception carries a profile-specific actionable message.
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ShellPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.WorkingDirectory);
        if (profile.Arguments.Any(argument => argument is null) ||
            profile.EnvironmentVariables.Any(variable => string.IsNullOrWhiteSpace(variable.Key) || variable.Value is null))
        {
            throw new ArgumentException("Shell profile arguments and environment variables cannot contain null values.", nameof(profile));
        }
#pragma warning restore IDE0046

        return profile with
        {
            Id = profile.Id.Trim(),
            DisplayName = profile.DisplayName.Trim(),
            ShellPath = profile.ShellPath.Trim(),
            WorkingDirectory = profile.WorkingDirectory.Trim(),
            Arguments = [.. profile.Arguments],
            EnvironmentVariables = new Dictionary<string, string>(profile.EnvironmentVariables, StringComparer.Ordinal),
        };
    }

    public void Dispose()
    {
        _initializeGate.Dispose();
    }
}
