using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>The conflict resolver holds policy and no Avalonia, which is what makes these plain
/// <c>[Fact]</c>s rather than a headless window harness.</summary>
public sealed class TransferConflictResolverTests
{
    [Fact]
    public async Task AnswerOnceWithApplyToAllAsksTheDialogExactlyOnceForFiveItems()
    {
        var token = TestContext.Current.CancellationToken;
        var dialogs = new StorageTestDoubles.RecordingConflictDialog
        {
            Decision = TransferConflictDecision.Overwrite,
            ApplyToAll = true,
        };
        var resolver = CreateResolver(dialogs, batchSize: 5);

        var decisions = new List<TransferConflictDecision>();
        for (var index = 0; index < 5; index++)
        {
            decisions.Add(await resolver.ResolveAsync(Conflict($"file-{index}.bin"), token));
        }

        // The object's lifetime is the batch. AsyncLocal cannot do this: QueueAsync fires and forgets, so
        // the gesture's stack has returned long before the queued transfers ask.
        Assert.Equal(1, dialogs.ShowCount);
        Assert.All(decisions, decision => Assert.Equal(TransferConflictDecision.Overwrite, decision));
        Assert.True(Assert.Single(dialogs.OfferedApplyToAll));
    }

    [Fact]
    public async Task WithoutApplyToAllEveryItemIsAskedAbout()
    {
        var token = TestContext.Current.CancellationToken;
        var dialogs = new StorageTestDoubles.RecordingConflictDialog
        {
            Decision = TransferConflictDecision.Skip,
            ApplyToAll = false,
        };
        var resolver = CreateResolver(dialogs, batchSize: 3);

        for (var index = 0; index < 3; index++)
        {
            _ = await resolver.ResolveAsync(Conflict($"file-{index}.bin"), token);
        }

        Assert.Equal(3, dialogs.ShowCount);
    }

    [Fact]
    public async Task ApplyToAllIsNotOfferedForABatchOfOne()
    {
        var token = TestContext.Current.CancellationToken;
        var dialogs = new StorageTestDoubles.RecordingConflictDialog();
        var resolver = CreateResolver(dialogs, batchSize: 1);

        _ = await resolver.ResolveAsync(Conflict("only.bin"), token);

        // "Apply to all" on a batch of one means "apply to this one thing", which reads as a trick
        // question.
        Assert.False(Assert.Single(dialogs.OfferedApplyToAll));
    }

    [Theory]
    [InlineData(StorageConflictDefault.Overwrite, TransferConflictDecision.Overwrite)]
    [InlineData(StorageConflictDefault.Skip, TransferConflictDecision.Skip)]
    public async Task AConfiguredDefaultAnswersWithoutAskingAtAll(
        StorageConflictDefault configured,
        TransferConflictDecision expected)
    {
        var token = TestContext.Current.CancellationToken;
        var dialogs = new StorageTestDoubles.RecordingConflictDialog();
        var resolver = CreateResolver(dialogs, batchSize: 4, configured);

        var decision = await resolver.ResolveAsync(Conflict("thing.bin"), token);

        // Overwrite is the default because a put is atomic and idempotent in both providers, and a user
        // who dropped a file onto a prefix that already holds that key overwhelmingly means replace. It is
        // a setting rather than a constant precisely because an unversioned bucket makes it unrecoverable.
        Assert.Equal(expected, decision);
        Assert.Equal(0, dialogs.ShowCount);
    }

    private static ITransferConflictResolver CreateResolver(
        ITransferConflictDialogService dialogs,
        int batchSize,
        StorageConflictDefault configured = StorageConflictDefault.Prompt)
    {
        var settings = new InMemorySettingsStore();
        settings.Set(SettingKeys.StorageConflictDefault, configured, CancellationToken.None)
            .GetAwaiter().GetResult();
        return new TransferConflictResolverFactory(dialogs, settings).Create(batchSize);
    }

    private static TransferConflict Conflict(string name)
    {
        return new TransferConflict(TransferDirection.Upload, $"C:\\local\\{name}", $"/media/{name}", 42);
    }
}
