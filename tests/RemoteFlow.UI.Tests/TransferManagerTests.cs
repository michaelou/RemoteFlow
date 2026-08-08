using System.Diagnostics;
using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.UI.Navigation;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels;
using RemoteFlow.UI.ViewModels.CommandPalette;
using RemoteFlow.UI.ViewModels.Transfers;
using RemoteFlow.UI.Views.Transfers;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class TransferManagerTests
{
    [Fact]
    public async Task TwentyQueuedTransfersRespectConcurrencyAndRemainResponsive()
    {
        using var manager = CreateManager(maxConcurrent: 3);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopwatch = Stopwatch.StartNew();
        var items = new List<TransferItemViewModel>();
        for (var index = 0; index < 20; index++)
        {
            var id = index;
            items.Add(await manager.QueueAsync(Request($"file-{id}.bin", async (_, token) =>
            {
                await release.Task.WaitAsync(token);
                return Completed($"file-{id}.bin");
            }), TestContext.Current.CancellationToken));
        }
        stopwatch.Stop();

        await WaitUntilAsync(() => manager.ActiveCount == 3 && manager.QueuedCount == 17);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Queueing took {stopwatch.Elapsed}.");
        Assert.Equal("3 active, 17 queued", manager.AggregateStatus);

        release.SetResult();
        _ = await Task.WhenAll(items.Select(item => item.Completion));
        await WaitUntilAsync(() => manager.CompletedCount == 20);
        Assert.Equal(0, manager.ActiveCount);
        Assert.Equal(0, manager.QueuedCount);
    }

    [Fact]
    public async Task PerItemCancelFailureAndRetryAllReachExpectedState()
    {
        using var manager = CreateManager(maxConcurrent: 1);
        var attempts = 0;
        var failed = await manager.QueueAsync(Request("retry.txt", (_, _) =>
        {
            attempts++;
            return Task.FromResult(attempts == 1
                ? Failed("retry.txt", "Disk quota exceeded.")
                : Completed("retry.txt"));
        }), TestContext.Current.CancellationToken);
        _ = await failed.Completion;
        await WaitUntilAsync(() => failed.Status == ManagedTransferStatus.Failed);
        Assert.Equal("Disk quota exceeded.", failed.FailureReason);
        Assert.True(failed.CanRetry);

        await manager.RetryAsync(failed);
        _ = await failed.Completion;
        await WaitUntilAsync(() => failed.Status == ManagedTransferStatus.Completed);
        Assert.Equal(2, attempts);

        var blocker = await manager.QueueAsync(Request("cancel.txt", async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Completed("cancel.txt");
        }), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => blocker.Status == ManagedTransferStatus.Active);
        blocker.CancelCommand.Execute(null);
        var cancelled = await blocker.Completion;
        Assert.True(cancelled.IsCancelled);
        await WaitUntilAsync(() => blocker.Status == ManagedTransferStatus.Cancelled);
        Assert.True(blocker.CanRetry);
    }

    [Fact]
    public async Task ClearCompletedNeverTouchesActiveItems()
    {
        using var manager = CreateManager(maxConcurrent: 2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = await manager.QueueAsync(Request("active.bin", async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return Completed("active.bin");
        }), TestContext.Current.CancellationToken);
        var completed = await manager.QueueAsync(Request("done.bin", (_, _) =>
            Task.FromResult(Completed("done.bin"))), TestContext.Current.CancellationToken);
        _ = await completed.Completion;
        await WaitUntilAsync(() => manager.ActiveCount == 1 && manager.CompletedCount == 1);

        manager.ClearCompleted();

        Assert.Contains(active, manager.Items);
        Assert.Equal(1, manager.ActiveCount);
        Assert.Empty(manager.CompletedItems);
        release.SetResult();
        _ = await active.Completion;
    }

    [Fact]
    public async Task FastProgressIsCoalescedAndAggregateUsesTheSamePanelState()
    {
        using var manager = CreateManager();
        var item = await manager.QueueAsync(Request("fast.bin", (progress, _) =>
        {
            for (var index = 0; index <= 10_000; index++)
            {
                progress.Report(new TransferProgress(
                    "fast.bin",
                    "fast.bin",
                    index,
                    10_000,
                    100_000,
                    TimeSpan.FromSeconds((10_000 - index) / 100_000d),
                    index == 10_000));
            }
            return Task.FromResult(Completed("fast.bin"));
        }), TestContext.Current.CancellationToken);

        _ = await item.Completion;
        await WaitUntilAsync(() => item.Status == ManagedTransferStatus.Completed);
        Assert.InRange(item.ProgressUpdateCount, 1, 3);
        Assert.Equal(10_000, item.BytesTransferred);
        var shell = new MainWindowViewModel(
            NavigationService.CreateDefault(),
            new CommandPaletteViewModel(),
            null,
            manager);
        Assert.Same(manager, shell.Transfers);
        Assert.NotNull(shell.Transfers);
        Assert.Equal(manager.AggregateStatus, shell.Transfers.AggregateStatus);
    }

    [Fact]
    public async Task CompletedDownloadCanRevealItsFolder()
    {
        var reveal = new StubReveal();
        using var manager = CreateManager(reveal: reveal);
        var item = await manager.QueueAsync(new TransferQueueRequest(
            TransferDirection.Download,
            "/remote/report.csv",
            "C:\\Downloads\\report.csv",
            (_, _) => Task.FromResult(Completed("report.csv"))), TestContext.Current.CancellationToken);
        _ = await item.Completion;
        await WaitUntilAsync(() => item.CanReveal);

        await manager.RevealAsync(item);

        Assert.Equal("C:\\Downloads\\report.csv", Assert.Single(reveal.Paths));
    }

    [AvaloniaFact]
    public void EmptyTransferViewHasARealEmptyState()
    {
        using var manager = CreateManager();
        var view = new TransfersView { DataContext = manager };

        Assert.True(manager.IsEmpty);
        Assert.Equal("No active transfers", manager.AggregateStatus);
        Assert.NotNull(view);
    }

    private static TransfersPageViewModel CreateManager(
        int maxConcurrent = 3,
        StubReveal? reveal = null)
    {
        return new TransfersPageViewModel(
            new InlineDispatcher(),
            reveal ?? new StubReveal(),
            maxConcurrent);
    }

    private static TransferQueueRequest Request(
        string name,
        Func<IProgress<TransferProgress>, CancellationToken, Task<TransferResult>> operation)
    {
        return new TransferQueueRequest(TransferDirection.Upload, name, $"/remote/{name}", operation);
    }

    private static TransferResult Completed(string name)
    {
        return new TransferResult([new TransferItemResult(
            name,
            $"/remote/{name}",
            TransferItemStatus.Completed)]);
    }

    private static TransferResult Failed(string name, string reason)
    {
        return new TransferResult([new TransferItemResult(
            name,
            $"/remote/{name}",
            TransferItemStatus.Failed,
            new SftpFailure(SftpError.QuotaExceeded, reason))]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timeout.Elapsed < TimeSpan.FromSeconds(3), "The transfer state did not settle in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubReveal : IFileRevealService
    {
        public List<string> Paths { get; } = [];

        public Task<FileRevealResult> RevealAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            Paths.Add(filePath);
            return Task.FromResult(FileRevealResult.Success);
        }
    }
}
