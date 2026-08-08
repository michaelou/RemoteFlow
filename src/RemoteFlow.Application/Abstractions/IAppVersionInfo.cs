using System.Reflection;

namespace RemoteFlow.Application.Abstractions;

/// <summary>The version of the running build and the commit it was built from, so a release artefact or a
/// bug report can be traced back to one commit.</summary>
public interface IAppVersionInfo
{
    /// <summary>The SemVer version, for example <c>0.1.0</c> or <c>0.0.0-alpha.0.57</c>.</summary>
    string Version { get; }

    /// <summary>The full commit hash, or <see langword="null"/> when the build had no source control
    /// information (a source tree exported without <c>.git</c>).</summary>
    string? CommitSha { get; }
}

/// <summary>Reads the version MinVer wrote into <see cref="AssemblyInformationalVersionAttribute"/>. The
/// SDK appends <c>+commit</c> to that attribute, which is the only place the commit is recorded.</summary>
public sealed class AssemblyVersionInfo : IAppVersionInfo
{
    private const string _fallbackVersion = "0.0.0";

    private AssemblyVersionInfo(string version, string? commitSha)
    {
        Version = version;
        CommitSha = commitSha;
    }

    public string Version { get; }

    public string? CommitSha { get; }

    /// <summary>The version of the assembly that started the process.</summary>
    public static AssemblyVersionInfo ForEntryAssembly()
    {
        return ForAssembly(Assembly.GetEntryAssembly());
    }

    public static AssemblyVersionInfo ForAssembly(Assembly? assembly)
    {
        var informationalVersion = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return Parse(informationalVersion);
    }

    /// <summary>Splits an informational version into its version and commit parts. A build without source
    /// control information has no <c>+commit</c> suffix, and a build from an unversioned assembly has
    /// nothing at all; both are reported rather than throwing, because printing the wrong version is worse
    /// than printing an obviously absent one.</summary>
    public static AssemblyVersionInfo Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return new AssemblyVersionInfo(_fallbackVersion, null);
        }

        var trimmed = informationalVersion.Trim();
        var separator = trimmed.IndexOf('+', StringComparison.Ordinal);
        if (separator < 0)
        {
            return new AssemblyVersionInfo(trimmed, null);
        }

        var version = trimmed[..separator];
        var commit = trimmed[(separator + 1)..];
        return new AssemblyVersionInfo(
            version.Length == 0 ? _fallbackVersion : version,
            commit.Length == 0 ? null : commit);
    }
}
