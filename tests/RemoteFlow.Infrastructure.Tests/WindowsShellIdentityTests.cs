using RemoteFlow.Infrastructure.Platform;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed class WindowsShellIdentityTests
{
    [Fact]
    public void ApplyClaimsTheIdentityOnWindowsAndIsANoOpElsewhere()
    {
        var applied = WindowsShellIdentity.Apply();

        Assert.Equal(OperatingSystem.IsWindows(), applied);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ApplyRejectsAnEmptyIdentity(string appUserModelId)
    {
        _ = Assert.Throws<ArgumentException>(() => WindowsShellIdentity.Apply(appUserModelId));
    }
}
