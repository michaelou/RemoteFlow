using System.Reflection;
using RemoteFlow.Application.Abstractions;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class AppVersionTests
{
    [Theory]
    [InlineData("0.1.0+3272ddcdf001f1472bb7358c5e3dd284c19482e3", "0.1.0", "3272ddcdf001f1472bb7358c5e3dd284c19482e3")]
    [InlineData("0.0.0-alpha.0.57+abc1234", "0.0.0-alpha.0.57", "abc1234")]
    public void InformationalVersionSplitsIntoVersionAndCommit(string informational, string version, string commit)
    {
        var parsed = AssemblyVersionInfo.Parse(informational);

        Assert.Equal(version, parsed.Version);
        Assert.Equal(commit, parsed.CommitSha);
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.1.0+")]
    public void ABuildWithoutSourceControlInformationReportsNoCommit(string informational)
    {
        var parsed = AssemblyVersionInfo.Parse(informational);

        Assert.Equal("0.1.0", parsed.Version);
        Assert.Null(parsed.CommitSha);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnversionedAssemblyFallsBackRatherThanThrowing(string? informational)
    {
        var parsed = AssemblyVersionInfo.Parse(informational);

        Assert.Equal("0.0.0", parsed.Version);
        Assert.Null(parsed.CommitSha);
    }

    [Fact]
    public void TheApplicationAssemblyCarriesTheVersionMinVerComputed()
    {
        var parsed = AssemblyVersionInfo.ForAssembly(typeof(AssemblyMarker).Assembly);

        // 1.0.0 is the SDK's default, which is what appears when MinVer is not wired in at all.
        Assert.NotEqual("1.0.0", parsed.Version);
        Assert.NotEqual("0.0.0", parsed.Version);
    }

    [Fact]
    public void AMissingAssemblyFallsBackRatherThanThrowing()
    {
        var parsed = AssemblyVersionInfo.ForAssembly(null);

        Assert.Equal("0.0.0", parsed.Version);
        Assert.Null(parsed.CommitSha);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--VERSION")]
    public void TheVersionSwitchIsRecognisedWhateverItsCase(string argument)
    {
        Assert.True(VersionSwitch.IsRequested([argument]));
        Assert.True(VersionSwitch.IsRequested(["--other", argument]));
    }

    [Fact]
    public void NoVersionSwitchMeansTheUiStarts()
    {
        Assert.False(VersionSwitch.IsRequested(null));
        Assert.False(VersionSwitch.IsRequested([]));
        Assert.False(VersionSwitch.IsRequested(["--versions", "version", "-version"]));
    }

    [Fact]
    public void TheVersionLineNamesTheProductTheVersionAndTheCommit()
    {
        var line = VersionSwitch.Format(AssemblyVersionInfo.Parse("0.1.0+abc1234"));

        Assert.Equal("RemoteFlow 0.1.0 (commit abc1234)", line);
    }

    [Fact]
    public void AMissingCommitIsSaidOutLoudRatherThanLeftBlank()
    {
        var line = VersionSwitch.Format(AssemblyVersionInfo.Parse("0.1.0"));

        Assert.Equal("RemoteFlow 0.1.0 (commit unknown)", line);
    }

    [Fact]
    public void TheEntryAssemblyIsReadWithoutThrowingWhereverTestsRunFrom()
    {
        var parsed = AssemblyVersionInfo.ForEntryAssembly();

        Assert.NotNull(parsed.Version);
        Assert.NotEmpty(parsed.Version);
    }

    [Fact]
    public void EveryProductAssemblyRecordsTheSameVersionAndCommit()
    {
        var assemblies = new[]
        {
            typeof(AssemblyMarker).Assembly,
            typeof(Domain.AssemblyMarker).Assembly,
        };

        var stamps = assemblies
            .Select(assembly => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _ = Assert.Single(stamps);
    }
}
