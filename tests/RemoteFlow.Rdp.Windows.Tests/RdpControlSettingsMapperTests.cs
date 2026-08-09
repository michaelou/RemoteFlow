using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using Xunit;

namespace RemoteFlow.Rdp.Windows.Tests;

public sealed class RdpControlSettingsMapperTests
{
    [Fact]
    public void MapsCompleteRepresentativeConnectionWithoutCredentials()
    {
        var connection = CreateConnection();
        _ = connection.ChangeEndpoint("rdp.example.com", 3391, ProtocolType.Rdp, SystemGuidProvider.Instance);
        _ = connection.SetDetails(
            "alice",
            AuthMethod.Password,
            notes: null,
            EnvironmentKind.Production,
            colorOverrideHex: null,
            SystemGuidProvider.Instance);
        var options = RdpOptions.Default();
        _ = options.Configure(
            domain: "EXAMPLE",
            fullScreen: true,
            width: 1920,
            height: 1080,
            multimon: true,
            redirectClipboard: false,
            redirectDrives: true);
        _ = connection.SetOptions(
            SshOptions.Default(),
            SftpOptions.Default(),
            options,
            SystemGuidProvider.Instance);

        var settings = RdpControlSettingsMapper.Map(connection, 800, 600, 1.4d);

        Assert.Equal("rdp.example.com", settings.Server);
        Assert.Equal(3391, settings.RdpPort);
        Assert.Equal("alice", settings.UserName);
        Assert.Equal("EXAMPLE", settings.Domain);
        Assert.Equal(1920, settings.DesktopWidth);
        Assert.Equal(1080, settings.DesktopHeight);
        Assert.Equal(32, settings.ColorDepth);
        Assert.False(settings.AdvancedSettings.RedirectClipboard);
        Assert.True(settings.AdvancedSettings.RedirectDrives);
        Assert.Equal(2u, settings.AdvancedSettings.AuthenticationLevel);
        Assert.True(settings.AdvancedSettings.EnableCredSspSupport);
        Assert.False(settings.AdvancedSettings.SmartSizing);
        Assert.Equal(RdpKeyboardHookMode.OnRemoteComputer, settings.AdvancedSettings.KeyboardHookMode);
        Assert.Equal(140u, settings.DesktopScaleFactor);
        Assert.Equal(140u, settings.DeviceScaleFactor);
        Assert.True(settings.IgnoredExternalDisplayOptions.FullScreenRequested);
        Assert.True(settings.IgnoredExternalDisplayOptions.MultiMonitorRequested);
        Assert.DoesNotContain(
            typeof(RdpControlSettings).GetProperties(),
            property => property.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(RdpControlAdvancedSettings).GetProperties(),
            property => property.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DefaultOptionsUseViewportSecureAuthenticationAndExpectedRedirection()
    {
        var settings = RdpControlSettingsMapper.Map(CreateConnection(), 1280, 720, 1d);

        Assert.Equal(1280, settings.DesktopWidth);
        Assert.Equal(720, settings.DesktopHeight);
        Assert.True(settings.AdvancedSettings.RedirectClipboard);
        Assert.False(settings.AdvancedSettings.RedirectDrives);
        Assert.Equal(2u, settings.AdvancedSettings.AuthenticationLevel);
        Assert.True(settings.AdvancedSettings.EnableCredSspSupport);
        Assert.False(settings.IgnoredExternalDisplayOptions.FullScreenRequested);
        Assert.False(settings.IgnoredExternalDisplayOptions.MultiMonitorRequested);
    }

    [Theory]
    [InlineData(0, 720)]
    [InlineData(-1, 720)]
    [InlineData(1280, 0)]
    [InlineData(1280, -1)]
    public void InvalidViewportCannotReachMappedSettings(int width, int height)
    {
        _ = Assert.Throws<ArgumentOutOfRangeException>(() =>
            RdpControlSettingsMapper.Map(CreateConnection(), width, height, 1d));
    }

    [Theory]
    [InlineData("EXAMPLE\\alice")]
    [InlineData("alice@example.com")]
    public void QualifiedAndUpnUserNamesArePreserved(string userName)
    {
        var connection = CreateConnection();
        _ = connection.SetDetails(
            userName,
            AuthMethod.Password,
            notes: null,
            EnvironmentKind.Unspecified,
            colorOverrideHex: null,
            SystemGuidProvider.Instance);

        var settings = RdpControlSettingsMapper.Map(connection, 1280, 720, 1d);

        Assert.Equal(userName, settings.UserName);
        Assert.Equal(2u, settings.AdvancedSettings.AuthenticationLevel);
        Assert.True(settings.AdvancedSettings.EnableCredSspSupport);
    }

    [Theory]
    [InlineData(0.75d, 100u)]
    [InlineData(1.00d, 100u)]
    [InlineData(1.20d, 100u)]
    [InlineData(1.25d, 140u)]
    [InlineData(1.40d, 140u)]
    [InlineData(1.50d, 140u)]
    [InlineData(1.60d, 140u)]
    [InlineData(1.80d, 180u)]
    [InlineData(2.00d, 180u)]
    [InlineData(2.25d, 180u)]
    public void DisplayScalingUsesNearestSupportedRdpFactor(double renderScaling, uint expected)
    {
        Assert.Equal(expected, RdpControlSettingsMapper.MapScaleFactor(renderScaling));
    }

    private static Connection CreateConnection()
    {
        return Connection.Create(
            SystemGuidProvider.Instance,
            "RDP server",
            "server.example.com",
            ProtocolType.Rdp).Value;
    }
}
