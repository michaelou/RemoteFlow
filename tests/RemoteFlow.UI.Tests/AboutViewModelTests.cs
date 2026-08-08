using RemoteFlow.Application.Abstractions;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.Settings;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class AboutViewModelTests
{
    [Fact]
    public void TheAboutBoxShowsTheVersionAndTheFullCommit()
    {
        var about = new AboutViewModel(AssemblyVersionInfo.Parse(
            "0.1.0+3272ddcdf001f1472bb7358c5e3dd284c19482e3"));

        Assert.Equal("RemoteFlow", about.ProductName);
        Assert.Equal("0.1.0", about.Version);
        Assert.Equal("3272ddcdf001f1472bb7358c5e3dd284c19482e3", about.Commit);
    }

    [Fact]
    public void ABuildWithoutACommitSaysUnknownRatherThanShowingNothing()
    {
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0"));

        Assert.Equal("unknown", about.Commit);
    }

    [Fact]
    public void TheSettingsPageCarriesTheAboutBox()
    {
        var about = new AboutViewModel(AssemblyVersionInfo.Parse("0.1.0+abc1234"));

        var settings = new SettingsPageViewModel(about: about);

        Assert.Same(about, settings.About);
    }
}
