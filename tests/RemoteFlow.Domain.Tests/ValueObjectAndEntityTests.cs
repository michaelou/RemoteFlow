using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Common;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.Domain.ValueObjects;
using Xunit;

namespace RemoteFlow.Domain.Tests;

public sealed class ValueObjectAndEntityTests
{
    [Fact]
    public void EmptyCredentialContainsNoStoreIdentity()
    {
        var credential = CredentialRef.None();

        Assert.True(credential.IsEmpty);
        Assert.Equal(CredentialKind.None, credential.Kind);
        Assert.Equal(string.Empty, credential.StoreKey);
        Assert.Equal(string.Empty, credential.StoreProvider);
    }

    [Theory]
    [InlineData(CredentialKind.None)]
    [InlineData((CredentialKind)99)]
    public void CredentialCreateRejectsNonConcreteKind(CredentialKind kind)
    {
        var result = CredentialRef.Create(kind, "key", "provider");

        Assert.True(result.IsFailure);
        Assert.Equal("credential.kind", result.Error.Code);
    }

    [Theory]
    [InlineData(null, "provider", "credential.store_key")]
    [InlineData("key", null, "credential.store_provider")]
    public void CredentialCreateRequiresStoreIdentity(string? key, string? provider, string code)
    {
        var result = CredentialRef.Create(CredentialKind.Password, key, provider);

        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error.Code);
    }

    [Fact]
    public void CredentialCreateNormalizesStoreIdentityAndTime()
    {
        var utc = DateTimeOffset.UtcNow;
        var result = CredentialRef.Create(
            CredentialKind.Password,
            " key ",
            " vault ",
            utc.ToOffset(TimeSpan.FromHours(3)));

        Assert.True(result.IsSuccess);
        Assert.Equal("key", result.Value.StoreKey);
        Assert.Equal("vault", result.Value.StoreProvider);
        Assert.Equal(utc, result.Value.UpdatedUtc);
    }

    [Fact]
    public void SshDefaultsMatchTerminalPolicy()
    {
        var options = SshOptions.Default();

        Assert.Equal("xterm-256color", options.TerminalType);
        Assert.Equal(HostKeyPolicy.Strict, options.HostKeyPolicy);
        Assert.True(options.RequestPty);
        Assert.Null(options.KeepAliveSeconds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void SshConfigureRejectsNonPositiveKeepAlive(int seconds)
    {
        var options = SshOptions.Default();

        var result = options.Configure(keepAliveSeconds: seconds);

        Assert.True(result.IsFailure);
        Assert.Null(options.KeepAliveSeconds);
    }

    [Fact]
    public void SshConfigureAppliesAllValues()
    {
        var options = SshOptions.Default();

        var result = options.Configure(
            30,
            " vt100 ",
            " key.pem ",
            " ls ",
            " /srv ",
            HostKeyPolicy.TrustOnFirstUse,
            false);

        Assert.True(result.IsSuccess);
        Assert.Equal(30, options.KeepAliveSeconds);
        Assert.Equal("vt100", options.TerminalType);
        Assert.Equal("key.pem", options.PrivateKeyPath);
        Assert.Equal("ls", options.InitialCommand);
        Assert.Equal("/srv", options.StartupDirectory);
        Assert.Equal(HostKeyPolicy.TrustOnFirstUse, options.HostKeyPolicy);
        Assert.False(options.RequestPty);
    }

    [Fact]
    public void SftpConfigureAppliesAllValues()
    {
        var options = SftpOptions.Default().Configure(" /srv ", " C:\\Downloads ", true, true);

        Assert.Equal("/srv", options.RemoteRootPath);
        Assert.Equal("C:\\Downloads", options.LocalDownloadPath);
        Assert.True(options.PreserveTimestamps);
        Assert.True(options.ShowHiddenFiles);
    }

    [Fact]
    public void RdpDefaultsRedirectClipboardOnly()
    {
        var options = RdpOptions.Default();

        Assert.True(options.RedirectClipboard);
        Assert.False(options.RedirectDrives);
        Assert.False(options.FullScreen);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    [InlineData(-1, 1080)]
    public void RdpConfigureRejectsNonPositiveDimensions(int width, int height)
    {
        var result = RdpOptions.Default().Configure(width: width, height: height);

        Assert.True(result.IsFailure);
        Assert.Equal("rdp.dimensions", result.Error.Code);
    }

    [Fact]
    public void RdpConfigureAppliesAllValues()
    {
        var options = RdpOptions.Default();

        var result = options.Configure(" DOMAIN ", true, 1920, 1080, true, false, true);

        Assert.True(result.IsSuccess);
        Assert.Equal("DOMAIN", options.Domain);
        Assert.True(options.FullScreen);
        Assert.Equal(1920, options.Width);
        Assert.Equal(1080, options.Height);
        Assert.True(options.Multimon);
        Assert.False(options.RedirectClipboard);
        Assert.True(options.RedirectDrives);
    }

    [Theory]
    [InlineData("#abcdef", "#ABCDEF")]
    [InlineData(null, null)]
    public void TagCreateNormalizesOptionalColor(string? color, string? expected)
    {
        var result = Tag.Create(GuidProvider(), " Ops ", color);

        Assert.True(result.IsSuccess);
        Assert.Equal("Ops", result.Value.Name);
        Assert.Equal(expected, result.Value.ColorHex);
    }

    [Fact]
    public void TagCreateRejectsInvalidColor()
    {
        var result = Tag.Create(GuidProvider(), "Ops", "blue");

        Assert.True(result.IsFailure);
        Assert.Equal("tag.color", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    public void HostKeyCreateRejectsInvalidPort(int port)
    {
        var result = CreateHostKey(port: port);

        Assert.True(result.IsFailure);
        Assert.Equal("host_key.port", result.Error.Code);
    }

    [Fact]
    public void HostKeyCreateRequiresSha256FingerprintPrefix()
    {
        var result = CreateHostKey(fingerprint: "MD5:abc");

        Assert.True(result.IsFailure);
        Assert.Equal("host_key.fingerprint", result.Error.Code);
    }

    [Fact]
    public void HostKeyObserveNeverMovesLastSeenBackward()
    {
        var firstSeen = DateTimeOffset.UtcNow;
        var hostKey = CreateHostKey(seenUtc: firstSeen).Value;

        _ = hostKey.Observe(firstSeen.AddMinutes(-1));
        Assert.Equal(firstSeen, hostKey.LastSeenUtc);

        _ = hostKey.Observe(firstSeen.AddMinutes(1));
        Assert.Equal(firstSeen.AddMinutes(1), hostKey.LastSeenUtc);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"value\"")]
    [InlineData(/*lang=json,strict*/ "{\"enabled\":true}")]
    [InlineData("[1,2,3]")]
    public void SettingAcceptsValidJson(string json)
    {
        var result = Setting.Create("ui.theme", json);

        Assert.True(result.IsSuccess);
        Assert.Equal(json, result.Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{")]
    public void SettingRejectsInvalidJson(string? json)
    {
        var result = Setting.Create("ui.theme", json);

        Assert.True(result.IsFailure);
        Assert.Equal("setting.value", result.Error.Code);
    }

    [Fact]
    public void RecentConnectionStartsAtOneAndIncrements()
    {
        var connectionId = Guid.CreateVersion7();
        var opened = DateTimeOffset.UtcNow;
        var recent = RecentConnection.Create(connectionId, opened).Value;

        _ = recent.RecordOpened(opened.AddMinutes(1));

        Assert.Equal(connectionId, recent.ConnectionId);
        Assert.Equal(2, recent.OpenCount);
        Assert.Equal(opened.AddMinutes(1), recent.LastOpenedUtc);
    }

    [Fact]
    public void RecentConnectionRejectsEmptyConnectionId()
    {
        var result = RecentConnection.Create(Guid.Empty);

        Assert.True(result.IsFailure);
        Assert.Equal("recent_connection.connection_id", result.Error.Code);
    }

    [Fact]
    public void SuccessfulResultHasValueAndNoAccessibleError()
    {
        var result = Result<string>.Success("ok");

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal("ok", result.Value);
        _ = Assert.Throws<InvalidOperationException>(() => result.Error);
    }

    [Fact]
    public void FailedResultHasErrorAndNoAccessibleValue()
    {
        var error = RemoteFlowError.Validation("code", "message");
        var result = Result<string>.Failure(error);

        Assert.True(result.IsFailure);
        Assert.Same(error, result.Error);
        _ = Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ConnectionTagRejectsEmptyConnectionId()
    {
        _ = Assert.Throws<ArgumentException>(() => new ConnectionTag(Guid.Empty, Guid.CreateVersion7()));
    }

    [Fact]
    public void ConnectionTagRejectsEmptyTagId()
    {
        _ = Assert.Throws<ArgumentException>(() => new ConnectionTag(Guid.CreateVersion7(), Guid.Empty));
    }

    private static Result<HostKey> CreateHostKey(
        int port = 22,
        string fingerprint = "SHA256:abc",
        DateTimeOffset? seenUtc = null)
    {
        return HostKey.Create(
            GuidProvider(),
            "host",
            port,
            "ssh-ed25519",
            "AAAA-key",
            fingerprint,
            HostKeyTrust.Trusted,
            HostKeySource.UserAccepted,
            seenUtc: seenUtc);
    }

    private static SystemGuidProvider GuidProvider()
    {
        return SystemGuidProvider.Instance;
    }
}
