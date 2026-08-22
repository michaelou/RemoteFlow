using System.Text;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Application.Queries;
using RemoteFlow.Application.Services;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.ViewModels.Storage;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>The dual-pane Storage page. The tests that matter most here are the two that pin decisions
/// rather than behaviour: there is exactly one transfer queue, and the two panes do not announce
/// themselves with the same name.</summary>
public sealed class StorageWorkspaceTests
{
    [Fact]
    public async Task BothPanesAreOnePaneClassOverDifferentSourcesAndNavigateIndependently()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(root, "sub"));
            await using var fixture = StorageTestDoubles.CreateFixture();
            fixture.Storage.Seed("/media/2024/clip.mov", Encoding.UTF8.GetBytes("frames"));
            await fixture.Page.AttachAsync(fixture.Connection.Id, token);
            _ = await fixture.Page.Local.NavigateAsync(root, token);

            _ = Assert.IsType<FileBrowserPaneViewModel>(fixture.Page.Local);
            _ = Assert.IsType<FileBrowserPaneViewModel>(fixture.Page.Remote);
            _ = Assert.IsType<LocalFileBrowserSource>(fixture.Page.Local.Source);
            _ = Assert.IsType<ObjectStorageFileBrowserSource>(fixture.Page.Remote.Source);

            var remoteBefore = fixture.Page.Remote.CurrentPath;
            _ = await fixture.Page.Local.NavigateAsync(Path.Combine(root, "sub"), token);

            // Navigating one pane leaves the other exactly where it was.
            Assert.Equal(remoteBefore, fixture.Page.Remote.CurrentPath);
            Assert.EndsWith("sub", fixture.Page.Local.CurrentPath, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheTransferQueueIsTheInjectedSingletonAndNotASecondQueue()
    {
        using var transfers = StorageTestDoubles.Transfers();
        var settings = new InMemorySettingsStore();
        var page = new StoragePageViewModel(
            new StorageTestDoubles.StubStorageSessionFactory(
                new UI.Services.StorageWorkspaceSession(
                    StorageTestDoubles.StorageConnection(),
                    new InMemoryObjectStorage())),
            new StorageTestDoubles.RecordingConfirmation(true),
            new UI.Services.TransferConflictResolverFactory(
                new StorageTestDoubles.RecordingConflictDialog(),
                settings),
            transfers);

        // Pins the no-second-queue decision. A second queue would mean two independent three-slot gates —
        // six concurrent transfers with neither aware of the other.
        Assert.Same(transfers, page.Transfers);
    }

    [Fact]
    public void TheTwoPanesDoNotAnnounceThemselvesWithTheSameName()
    {
        using var transfers = StorageTestDoubles.Transfers();
        var fixture = StorageTestDoubles.CreateFixture();

        // The trap the audit cannot catch: one control used twice gives both Refresh buttons the same
        // accessible name, AccessibleNameAuditTests passes, and a screen-reader user is lost.
        Assert.NotEqual(fixture.Page.Local.RefreshLabel, fixture.Page.Remote.RefreshLabel);
        Assert.Equal("Refresh the local folder", fixture.Page.Local.RefreshLabel);
        Assert.Equal("Refresh the remote prefix", fixture.Page.Remote.RefreshLabel);
        Assert.NotEqual(fixture.Page.Local.ListLabel, fixture.Page.Remote.ListLabel);
        Assert.NotEqual(fixture.Page.Local.PathLabel, fixture.Page.Remote.PathLabel);
        Assert.NotEqual(
            fixture.Page.Local.TransferAccessibleLabel,
            fixture.Page.Remote.TransferAccessibleLabel);
    }

    [Fact]
    public async Task UploadAndDownloadRoundTripThroughTheQueue()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var payload = Encoding.UTF8.GetBytes("payload bytes");
            await File.WriteAllBytesAsync(Path.Combine(root, "report.bin"), payload, token);
            await using var fixture = StorageTestDoubles.CreateFixture();
            fixture.Storage.Seed("/media/keep.txt", Encoding.UTF8.GetBytes("keep"));
            await fixture.Page.AttachAsync(fixture.Connection.Id, token);
            _ = await fixture.Page.Local.NavigateAsync(root, token);

            fixture.Page.Local.SetSelection(
                fixture.Page.Local.Items.Where(item => item.Name == "report.bin"));
            await fixture.Page.UploadAsync(token);

            Assert.Null(fixture.Page.ErrorMessage);
            Assert.Contains("media/report.bin", fixture.Storage.Keys);

            var download = CreateTempDirectory();
            try
            {
                _ = await fixture.Page.Local.NavigateAsync(download, token);
                fixture.Page.Remote.SetSelection(
                    fixture.Page.Remote.Items.Where(item => item.Name == "report.bin"));
                await fixture.Page.DownloadAsync(token);

                Assert.Null(fixture.Page.ErrorMessage);
                Assert.Equal(
                    payload,
                    await File.ReadAllBytesAsync(Path.Combine(download, "report.bin"), token));
            }
            finally
            {
                Directory.Delete(download, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CreatingAndDeletingAFolderIsConfirmationGated()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = StorageTestDoubles.CreateFixture(confirmationResult: false);
        fixture.Storage.Seed("/media/logs/one.txt", [1]);
        fixture.Storage.Seed("/media/logs/two.txt", [1]);
        await fixture.Page.AttachAsync(fixture.Connection.Id, token);

        fixture.Page.Remote.NewFolderName = "reports";
        Assert.True(await fixture.Page.Remote.CommitCreateFolderAsync(token));
        Assert.Contains("media/reports/", fixture.Storage.Keys);

        var logs = fixture.Page.Remote.Items.Single(item => item.Name == "logs");
        Assert.False(await fixture.Page.Remote.DeleteAsync([logs], token));

        // Refused, and the count in the question is the expanded one rather than "1 folder".
        Assert.Contains("media/logs/one.txt", fixture.Storage.Keys);
        Assert.Contains("3 item(s)", Assert.Single(fixture.Confirmation.Messages), StringComparison.Ordinal);

        fixture.Confirmation.Result = true;
        Assert.True(await fixture.Page.Remote.DeleteAsync([logs], token));
        Assert.DoesNotContain(fixture.Storage.Keys, key => key.StartsWith("media/logs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFolderTransferShowsACountedCancellableConfirmationFirst()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "batch"));
            for (var index = 0; index < 3; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(folder.FullName, $"f{index}.txt"), "x", token);
            }

            await using var fixture = StorageTestDoubles.CreateFixture(confirmationResult: false);
            await fixture.Page.AttachAsync(fixture.Connection.Id, token);
            _ = await fixture.Page.Local.NavigateAsync(root, token);
            fixture.Page.Local.SetSelection(fixture.Page.Local.Items.Where(item => item.Name == "batch"));

            await fixture.Page.UploadAsync(token);

            Assert.Contains("4 item(s)", Assert.Single(fixture.Confirmation.Messages), StringComparison.Ordinal);
            Assert.Empty(fixture.Storage.Keys);
            Assert.Contains("cancelled", fixture.Page.Local.FeedbackMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ATruncatedListingShowsTheCapInsteadOfLoadMoreAndSaysWhatTheSortCovers()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = StorageTestDoubles.CreateFixture();

        // One row per page, so the cap is reached after MaxPages rather than after ten thousand rows.
        fixture.Storage.PageSizeCap = 1;
        for (var index = 0; index < FileBrowserPaneViewModel.MaxPages + 5; index++)
        {
            fixture.Storage.Seed($"/media/object-{index:D2}.bin", [1]);
        }

        await fixture.Page.AttachAsync(fixture.Connection.Id, token);
        var pane = fixture.Page.Remote;
        Assert.Equal("Sorts the rows in this folder.", pane.SortScopeTooltip);

        while (pane.HasMore)
        {
            await pane.LoadMoreAsync(token);
        }

        Assert.True(pane.IsTruncated);
        Assert.False(pane.HasMore);
        Assert.Equal(FileBrowserPaneViewModel.MaxPages, pane.Items.Count);
        Assert.Contains("of many shown", pane.TruncationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("15", pane.TruncationMessage, StringComparison.Ordinal);

        // The thing most dual-pane cloud browsers get silently wrong, said out loud instead.
        Assert.Contains("rows loaded so far", pane.SortScopeTooltip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFilterBoxNarrowsAtTheSourceRatherThanFilteringLoadedRows()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = StorageTestDoubles.CreateFixture();
        fixture.Storage.Seed("/media/alpha-1.bin", [1]);
        fixture.Storage.Seed("/media/alpha-2.bin", [1]);
        fixture.Storage.Seed("/media/beta-1.bin", [1]);
        await fixture.Page.AttachAsync(fixture.Connection.Id, token);
        var listsBefore = fixture.Storage.ListCount;

        fixture.Page.Remote.FilterText = "alpha";
        await WaitUntilAsync(() => fixture.Page.Remote.Items.Count == 2, token);

        // Re-listed, not filtered: one request instead of a hundred, and the only kind of narrowing
        // either provider can actually do.
        Assert.True(fixture.Storage.ListCount > listsBefore);
        Assert.Equal(
            ["alpha-1.bin", "alpha-2.bin"],
            fixture.Page.Remote.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task ThePickerOffersOnlyObjectStorageAccountsAndOpensTheChosenOne()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = StorageTestDoubles.StorageConnection();
        var queries = new StorageTestDoubles.StubConnectionQueries(
        [
            new ConnectionListItem(
                connection.Id,
                "Objects",
                "s3.eu-west-2.amazonaws.com",
                443,
                ProtocolType.S3,
                EnvironmentKind.Unspecified,
                false,
                null,
                null,
                null,
                null,
                [],
                null),
        ]);
        await using var fixture = StorageTestDoubles.CreateFixture(connectionQueries: queries);

        Assert.False(fixture.Page.HasConnectionChoices);
        await fixture.Page.LoadConnectionsAsync(token);

        Assert.Equal([ProtocolType.S3, ProtocolType.AzureBlob], queries.LastFilter!.Protocols);
        var choice = Assert.Single(fixture.Page.AvailableConnections);
        Assert.Equal("Objects", choice.Name);
        Assert.False(fixture.Page.ConnectSelectedCommand.CanExecute(null));

        fixture.Page.SelectedConnection = choice;
        Assert.True(fixture.Page.ConnectSelectedCommand.CanExecute(null));
        await fixture.Page.ConnectSelectedCommand.ExecuteAsync(null);

        Assert.True(fixture.Page.IsConnected);
        Assert.Null(fixture.Page.ErrorMessage);
        Assert.True(fixture.Page.Remote.IsReady);
    }

    [Fact]
    public async Task TheLocalPaneOffersItsDrivesAndTheRemotePaneOffersNone()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = StorageTestDoubles.CreateFixture();
        fixture.Storage.Seed("/media/clip.mov", [1]);
        await fixture.Page.InitializeLocalAsync(token);
        await fixture.Page.AttachAsync(fixture.Connection.Id, token);

        Assert.NotEmpty(fixture.Page.Local.Roots);
        Assert.Empty(fixture.Page.Remote.Roots);
        Assert.False(fixture.Page.Remote.HasRoots);
        Assert.Equal(
            LocalFileBrowserSource.Roots().Count > 1,
            fixture.Page.Local.HasRoots);

        // The picker follows wherever navigation lands, so it never claims the wrong drive.
        var current = Assert.IsType<FileBrowserCrumb>(fixture.Page.Local.SelectedRoot);
        Assert.StartsWith(current.Path, fixture.Page.Local.CurrentPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChoosingADifferentDriveNavigatesThatPaneAndLeavesTheOtherAlone()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = StorageTestDoubles.CreateFixture();
        fixture.Storage.Seed("/media/clip.mov", [1]);
        await fixture.Page.InitializeLocalAsync(token);
        await fixture.Page.AttachAsync(fixture.Connection.Id, token);
        var pane = fixture.Page.Local;
        var other = pane.Roots.FirstOrDefault(root => !ReferenceEquals(root, pane.SelectedRoot));
        if (other is null)
        {
            // One drive is not a choice, so the picker is not offered at all. Nothing else to assert on a
            // machine with a single volume, and pretending otherwise would be a test that only ever runs
            // on the author's laptop.
            Assert.False(pane.HasRoots);
            return;
        }

        var remoteBefore = fixture.Page.Remote.CurrentPath;
        pane.SelectedRoot = other;
        await WaitUntilAsync(
            () => string.Equals(pane.CurrentPath, other.Path, StringComparison.OrdinalIgnoreCase),
            token);

        Assert.Equal(other.Path, pane.CurrentPath);
        Assert.Equal(remoteBefore, fixture.Page.Remote.CurrentPath);

        // Walking into a folder must not bounce the pane back to the drive root: the picker is told where
        // navigation landed, and that write does not turn round and navigate again.
        var folder = pane.Items.FirstOrDefault(item => item.IsDirectory);
        if (folder is not null)
        {
            _ = await pane.OpenAsync(folder, token);
            Assert.Equal(folder.Path, pane.CurrentPath);
            Assert.Equal(other.Path, pane.SelectedRoot!.Path);
        }
    }

    [Fact]
    public async Task TheRemotePaneOffersNoRenameAndNoHiddenToggle()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = StorageTestDoubles.CreateFixture();
        fixture.Storage.Seed("/media/clip.mov", [1]);
        await fixture.Page.AttachAsync(fixture.Connection.Id, token);

        Assert.True(fixture.Page.Local.SupportsRename);
        Assert.False(fixture.Page.Remote.SupportsRename);
        Assert.False(fixture.Page.Remote.SupportsHiddenEntries);

        var item = Assert.Single(fixture.Page.Remote.Items);
        fixture.Page.Remote.BeginRename(item);

        // Not faked: S3's nearest primitive is a billed, size-capped server-side copy plus a delete.
        Assert.False(item.IsRenaming);
        Assert.Contains("no rename", fixture.Page.Remote.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(5, cancellationToken);
        }

        Assert.True(condition(), "The expected state was never reached.");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "remoteflow-storage-" + Path.GetRandomFileName());
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
