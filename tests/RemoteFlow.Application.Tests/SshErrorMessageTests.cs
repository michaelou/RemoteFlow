using RemoteFlow.Application.Abstractions.Ssh;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class SshErrorMessageTests
{
    [Fact]
    public void EveryErrorHasDistinctActionableNonRawMessage()
    {
        var errors = Enum.GetValues<SshError>();
        var messages = errors.Select(SshErrorMessages.ToUserMessage).ToArray();

        Assert.Equal(errors.Length, messages.Distinct(StringComparer.Ordinal).Count());
        foreach (var (error, message) in errors.Zip(messages))
        {
            Assert.NotEqual(error.ToString(), message);
            Assert.DoesNotContain("Exception", message, StringComparison.Ordinal);
            Assert.Contains(".", message, StringComparison.Ordinal);
        }
    }
}
