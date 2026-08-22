using RemoteFlow.Domain.Enums;
using Xunit;

namespace RemoteFlow.Domain.Tests;

public sealed class EnumValueTests
{
    [Fact]
    public void ProtocolTypeValuesAreStable()
    {
        Assert.Equal([1, 2, 3, 4, 5], Enum.GetValues<ProtocolType>().Select(value => (int)value));
    }

    [Fact]
    public void AuthMethodValuesAreStable()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], Enum.GetValues<AuthMethod>().Select(value => (int)value));
    }

    [Fact]
    public void EnvironmentKindValuesAreStable()
    {
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<EnvironmentKind>().Select(value => (int)value));
    }

    [Fact]
    public void HostKeyPolicyValuesAreStable()
    {
        Assert.Equal([0, 1, 2], Enum.GetValues<HostKeyPolicy>().Select(value => (int)value));
    }

    [Fact]
    public void HostKeyTrustValuesAreStable()
    {
        Assert.Equal([1, 2], Enum.GetValues<HostKeyTrust>().Select(value => (int)value));
    }

    [Fact]
    public void HostKeySourceValuesAreStable()
    {
        Assert.Equal([1, 2, 3, 4, 5], Enum.GetValues<HostKeySource>().Select(value => (int)value));
    }

    [Fact]
    public void CredentialKindValuesAreStable()
    {
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<CredentialKind>().Select(value => (int)value));
    }

    [Fact]
    public void TerminalKindValuesAreStable()
    {
        Assert.Equal([1, 2], Enum.GetValues<TerminalKind>().Select(value => (int)value));
    }

    [Fact]
    public void SessionStateValuesAreStable()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], Enum.GetValues<SessionState>().Select(value => (int)value));
    }

    [Fact]
    public void ConflictResolutionValuesAreStable()
    {
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<ConflictResolution>().Select(value => (int)value));
    }

    [Fact]
    public void MergeStrategyValuesAreStable()
    {
        Assert.Equal([1, 2], Enum.GetValues<MergeStrategy>().Select(value => (int)value));
    }

    [Fact]
    public void MergeConflictPolicyValuesAreStable()
    {
        Assert.Equal([1, 2, 3], Enum.GetValues<MergeConflictPolicy>().Select(value => (int)value));
    }

    [Fact]
    public void RemoteFlowErrorKindValuesAreStable()
    {
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9], Enum.GetValues<RemoteFlowErrorKind>().Select(value => (int)value));
    }
}
