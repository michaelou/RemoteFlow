using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Updates;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

/// <summary>This is the gate on self-update, and the case it exists for is the quiet one: a portable copy
/// extracted into the same folder an install would use, or an install that has been copied elsewhere. Both
/// look like an install to anything that only inspects the path, and running an installer for either would
/// replace files somebody else's copy is using.
///
/// Every case goes through the constructor that takes the registry answer, so no test reads a real hive and
/// the result does not depend on how this machine happens to be installed.</summary>
public sealed class AppInstallInfoTests
{
    private const string _installed = @"C:\Users\someone\AppData\Local\Programs\RemoteFlow";

    [Fact]
    public void AnInstallRunningWhereItsUninstallEntrySaysItIsCanUpdateItself()
    {
        var info = Create(_installed, _installed);

        Assert.Equal(InstallShape.Installer, info.Shape);
        Assert.Null(info.Explanation);
        Assert.Equal(_installed, info.InstallDirectory);
    }

    // Inno writes App Path without a trailing separator and AppContext.BaseDirectory always has one, so
    // this is the shape the production path actually compares.
    [Fact]
    public void ATrailingSeparatorAndADifferentCaseStillMatch()
    {
        var info = Create($@"{_installed}\", @"c:\users\someone\appdata\local\programs\REMOTEFLOW");

        Assert.Equal(InstallShape.Installer, info.Shape);
    }

    [Fact]
    public void ACopyWithNoUninstallEntryIsPortableAndSaysWhatToDoInstead()
    {
        var info = Create(@"D:\Tools\RemoteFlow", installedPath: null);

        Assert.Equal(InstallShape.Portable, info.Shape);
        Assert.Contains("portable copy", info.Explanation!, StringComparison.Ordinal);
    }

    /// <summary>The case an installer would get wrong and a path check would miss: RemoteFlow is installed,
    /// but this is not that copy. Upgrading would replace the other one and leave this folder stale.</summary>
    [Fact]
    public void ACopyRunningSomewhereOtherThanTheInstallNamesBothDirectories()
    {
        var info = Create(@"D:\Tools\RemoteFlow", _installed);

        Assert.Equal(InstallShape.Portable, info.Shape);
        var explanation = Assert.IsType<string>(info.Explanation);
        Assert.Contains(@"D:\Tools\RemoteFlow", explanation, StringComparison.Ordinal);
        Assert.Contains(_installed, explanation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Projects\RemoteFlow\src\RemoteFlow.Desktop\bin\Debug\net10.0")]
    [InlineData(@"C:\Projects\RemoteFlow\src\RemoteFlow.Desktop\bin\Release\net10.0")]
    public void ABuildOutputDirectoryIsNeverUpgradedOverEvenWhenAnInstallExists(string directory)
    {
        // The registry says RemoteFlow is installed, because on a developer's machine it usually is. That
        // must not turn `dotnet run` into something an installer will overwrite.
        var info = Create(directory, _installed);

        Assert.Equal(InstallShape.Development, info.Shape);
        Assert.Contains("build output", info.Explanation!, StringComparison.Ordinal);
    }

    [Fact]
    public void OffWindowsThereIsNoInstallerToRunAndTheRegistryIsNeverConsulted()
    {
        var consulted = false;
        var info = new AppInstallInfo(
            new StubPlatform(OperatingSystemFamily.Linux),
            "/usr/local/lib/remoteflow",
            () =>
            {
                consulted = true;
                return _installed;
            });

        Assert.Equal(InstallShape.Development, info.Shape);
        Assert.False(consulted);
    }

    private static AppInstallInfo Create(string baseDirectory, string? installedPath)
    {
        return new AppInstallInfo(
            new StubPlatform(OperatingSystemFamily.Windows),
            baseDirectory,
            () => installedPath);
    }

    private sealed class StubPlatform(OperatingSystemFamily family) : ISystemPlatform
    {
        public OperatingSystemFamily OperatingSystem { get; } = family;

        public string CurrentDirectory => throw new NotSupportedException();

        public string HomeDirectory => throw new NotSupportedException();

        public string? GetEnvironmentVariable(string name)
        {
            throw new NotSupportedException();
        }

        public string? FindExecutable(string name)
        {
            throw new NotSupportedException();
        }

        public bool FileExists(string path)
        {
            throw new NotSupportedException();
        }

        public string? GetLoginShellFromPasswd()
        {
            throw new NotSupportedException();
        }
    }
}
