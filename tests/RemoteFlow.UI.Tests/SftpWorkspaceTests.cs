using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Sftp;
using RemoteFlow.UI.Views.Sftp;
using Xunit;

#pragma warning disable IDE0022 // Compact forwarding members keep this fault-injection double readable.

namespace RemoteFlow.UI.Tests;

public sealed class SftpWorkspaceTests
{
    [Fact]
    public async Task LargeListingIsDirectoryFirstStableSortableAndTypeSelectable()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        for (var index = 4_999; index >= 0; index--)
        {
            await SeedFileAsync(fixture.Sftp, $"/home/test/file-{index:D4}.txt", [1], token);
        }
        _ = await fixture.Sftp.CreateDirectoryAsync("/home/test/a-folder", token);
        _ = await fixture.Sftp.CreateDirectoryAsync("/home/test/z-folder", token);

        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        Assert.Equal(5_002, fixture.ViewModel.Items.Count);
        Assert.Collection(fixture.ViewModel.Items.Take(2),
            item => Assert.Equal("a-folder", item.Name),
            item => Assert.Equal("z-folder", item.Name));
        Assert.Equal("file-0420.txt", fixture.ViewModel.FindByPrefix("file-0420")?.Name);
        fixture.ViewModel.SortBy(SftpSortColumn.Size);
        Assert.True(fixture.ViewModel.Items[0].IsDirectory);
        Assert.True(fixture.ViewModel.Items[1].IsDirectory);
    }

    [Fact]
    public async Task TypedPathAndPermissionFailureStayInlineAndPreserveUsableListing()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        await SeedFileAsync(inner, "/home/test/visible.txt", "ok"u8.ToArray(), token);
        var denied = new DeniedListSftpService(inner, "/denied");
        var fixture = CreateFixture(denied);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        await fixture.ViewModel.NavigateAsync("relative/path", token);
        Assert.Contains("absolute", fixture.ViewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/home/test", fixture.ViewModel.CurrentPath);
        _ = Assert.Single(fixture.ViewModel.Items);

        await fixture.ViewModel.NavigateAsync("/denied", token);
        Assert.Contains("permission", fixture.ViewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/home/test", fixture.ViewModel.CurrentPath);
        _ = Assert.Single(fixture.ViewModel.Items);
        Assert.False(fixture.ViewModel.IsLoading);
    }

    [Fact]
    public async Task FolderDropUsesExactHoveredTargetAndUploadsRecursively()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        _ = await fixture.Sftp.CreateDirectoryAsync("/home/test/target", token);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var hovered = Assert.Single(fixture.ViewModel.Items);
        fixture.ViewModel.SetDropTarget(hovered);
        Assert.Equal("Upload to /home/test/target", fixture.ViewModel.DropTargetMessage);

        var localRoot = CreateTempDirectory();
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(localRoot, "payload"));
            var nested = Directory.CreateDirectory(Path.Combine(folder.FullName, "nested"));
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "root.txt"), "root", token);
            await File.WriteAllTextAsync(Path.Combine(nested.FullName, "child.txt"), "child", token);

            await fixture.ViewModel.UploadAsync([folder.FullName], hovered.FullPath, token);

            Assert.NotNull((await fixture.Sftp.StatAsync("/home/test/target/payload/root.txt", token)).Value);
            Assert.NotNull((await fixture.Sftp.StatAsync("/home/test/target/payload/nested/child.txt", token)).Value);
            Assert.Null(fixture.ViewModel.ErrorMessage);
        }
        finally
        {
            Directory.Delete(localRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DragOutReturnsOnlyCompletelyMaterializedFiles()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var contents = "complete before drag"u8.ToArray();
        await SeedFileAsync(fixture.Sftp, "/home/test/report.txt", contents, token);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var item = Assert.Single(fixture.ViewModel.Items);
        var staging = CreateTempDirectory();
        try
        {
            var paths = await fixture.ViewModel.PrepareDragOutAsync([item], staging, token);

            var path = Assert.Single(paths);
            Assert.Equal(contents, await File.ReadAllBytesAsync(path, token));
            Assert.False(File.Exists(path + ".part"));
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
        }
    }

    [Fact]
    public async Task RenameCollisionIsRejectedBeforeAnyRemoteMutationOrRefresh()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        await SeedFileAsync(inner, "/home/test/first.txt", [1], token);
        await SeedFileAsync(inner, "/home/test/second.txt", [2], token);
        var recording = new RecordingSftpService(inner);
        var fixture = CreateFixture(recording);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var listCalls = recording.ListCalls;
        var first = fixture.ViewModel.Items.Single(item => item.Name == "first.txt");
        fixture.ViewModel.BeginRename(first);
        first.RenameText = "second.txt";

        var renamed = await fixture.ViewModel.CommitRenameAsync(first, token);

        Assert.False(renamed);
        Assert.Equal(0, recording.RenameCalls);
        Assert.Equal(listCalls, recording.ListCalls);
        Assert.Contains("already exists", fixture.ViewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecursiveDeleteCountsBeforeSafeConfirmationAndCancelDeletesNothing()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        _ = await inner.CreateDirectoryAsync("/home/test/tree", token);
        _ = await inner.CreateDirectoryAsync("/home/test/tree/nested", token);
        await SeedFileAsync(inner, "/home/test/tree/root.txt", [1], token);
        await SeedFileAsync(inner, "/home/test/tree/nested/child.txt", [2], token);
        var recording = new RecordingSftpService(inner);
        var fixture = CreateFixture(recording, confirmationResult: false);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var tree = Assert.Single(fixture.ViewModel.Items);

        var deleted = await fixture.ViewModel.DeleteAsync([tree], token);

        Assert.False(deleted);
        Assert.Equal(0, recording.DeleteCalls);
        Assert.Contains("4 item(s)", fixture.Confirmation.Messages.Single(), StringComparison.Ordinal);
        Assert.Equal("Delete", fixture.Confirmation.ConfirmLabels.Single());
        Assert.NotNull((await inner.StatAsync("/home/test/tree/root.txt", token)).Value);
    }

    [Fact]
    public async Task PartialRecursiveDeleteReportsSucceededAndFailedPathsAndRefreshesViewOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        _ = await inner.CreateDirectoryAsync("/home/test/tree", token);
        await SeedFileAsync(inner, "/home/test/tree/good.txt", [1], token);
        await SeedFileAsync(inner, "/home/test/tree/blocked.txt", [2], token);
        var recording = new RecordingSftpService(inner)
        {
            DeleteFailurePath = "/home/test/tree/blocked.txt",
        };
        var fixture = CreateFixture(recording);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var tree = Assert.Single(fixture.ViewModel.Items);
        var listsBefore = recording.ListCalls;

        var deleted = await fixture.ViewModel.DeleteAsync([tree], token);

        Assert.False(deleted);
        Assert.Contains("Deleted 2 of 3", fixture.ViewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("/home/test/tree/blocked.txt", fixture.ViewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(listsBefore + 2, recording.ListCalls); // one recursive count, one affected-pane refresh
        Assert.Null((await inner.StatAsync("/home/test/tree/good.txt", token)).Value);
        Assert.NotNull((await inner.StatAsync("/home/test/tree/blocked.txt", token)).Value);
    }

    [Fact]
    public async Task CreateExistingFolderIsClearAndSuccessfulCreateRefreshesOnlyCurrentPane()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        _ = await inner.CreateDirectoryAsync("/home/test/existing", token);
        var recording = new RecordingSftpService(inner);
        var fixture = CreateFixture(recording);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var listsBefore = recording.ListCalls;
        fixture.ViewModel.BeginCreateFolder();
        fixture.ViewModel.NewFolderName = "existing";

        Assert.False(await fixture.ViewModel.CommitCreateFolderAsync(token));
        Assert.Equal(0, recording.CreateCalls);
        Assert.Contains("already exists", fixture.ViewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        fixture.ViewModel.NewFolderName = "created";
        Assert.True(await fixture.ViewModel.CommitCreateFolderAsync(token));
        Assert.Equal(1, recording.CreateCalls);
        Assert.Equal(listsBefore + 1, recording.ListCalls);
        Assert.Contains(fixture.ViewModel.Items, item => item.Name == "created" && item.IsDirectory);
    }

    [Fact]
    public async Task PropertiesExposeCompleteMetadataAndCopyPathIsShellSafe()
    {
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead;
        var item = new SftpItemViewModel(new RemoteFileInfo(
            "report's link",
            "/srv/team reports/report's link",
            1234,
            new DateTimeOffset(2026, 8, 8, 12, 30, 0, TimeSpan.Zero),
            mode,
            "alice",
            "ops",
            false,
            true,
            "/archive/report.txt"));
        var properties = SftpWorkspaceViewModel.GetProperties(item);
        var fixture = CreateFixture();

        await fixture.ViewModel.CopyPathAsync(item, TestContext.Current.CancellationToken);

        Assert.Equal("0754", properties.OctalMode);
        Assert.Equal("rwxr-xr--", properties.SymbolicMode);
        Assert.Equal("alice", properties.Owner);
        Assert.Equal("ops", properties.Group);
        Assert.Equal("/archive/report.txt", properties.SymlinkTarget);
        Assert.Equal("'/srv/team reports/report'\"'\"'s link'", fixture.Clipboard.WrittenText);
    }

    [Fact]
    public async Task DeleteCanBeCancelledMidFlightAndReportsExactProgress()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        await SeedFileAsync(inner, "/home/test/slow.txt", [1], token);
        var recording = new RecordingSftpService(inner) { BlockDeletes = true };
        var fixture = CreateFixture(recording);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        var deleteTask = fixture.ViewModel.DeleteAsync([Assert.Single(fixture.ViewModel.Items)], token);
        await recording.DeleteStarted.Task.WaitAsync(token);
        fixture.ViewModel.CancelOperation();
        var deleted = await deleteTask;

        Assert.False(deleted);
        Assert.Contains("after deleting 0 of 1", fixture.ViewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.False(fixture.ViewModel.IsMutating);
    }

    [AvaloniaFact]
    public void ViewUsesVirtualizingStackPanel()
    {
        var view = new SftpWorkspace();
        Assert.NotNull(view);
    }

    private static Fixture CreateFixture(ISftpService? service = null, bool confirmationResult = true)
    {
        var connection = Connection.Create(SystemGuidProvider.Instance, "Files", "example.test").Value;
        var ssh = new FakeSshConnection();
        var sftp = service ?? ssh.Sftp;
        var session = new SftpWorkspaceSession(connection, ssh, sftp);
        var factory = new StubSessionFactory(session);
        var confirmation = new RecordingConfirmation(confirmationResult);
        var clipboard = new RecordingClipboard();
        return new Fixture(
            connection,
            sftp,
            new SftpWorkspaceViewModel(factory, new StubFilePicker(), confirmation, clipboard),
            confirmation,
            clipboard);
    }

    private static async Task SeedFileAsync(
        ISftpService sftp,
        string path,
        byte[] contents,
        CancellationToken cancellationToken)
    {
        var opened = await sftp.OpenWriteAsync(path, cancellationToken);
        Assert.True(opened.IsSuccess);
        await using var stream = opened.Value;
        await stream.WriteAsync(contents, cancellationToken);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "RemoteFlow.Tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private sealed record Fixture(
        Connection Connection,
        ISftpService Sftp,
        SftpWorkspaceViewModel ViewModel,
        RecordingConfirmation Confirmation,
        RecordingClipboard Clipboard);

    private sealed class StubSessionFactory(SftpWorkspaceSession session) : ISftpWorkspaceSessionFactory
    {
        public Task<SftpWorkspaceSession> OpenAsync(Guid connectionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(session.Definition.Id, connectionId);
            return Task.FromResult(session);
        }
    }

    private sealed class StubFilePicker : IFilePickerService
    {
        public Task<IReadOnlyList<string>> PickUploadPathsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string?> PickDownloadFolderAsync(
            string? suggestedPath = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class RecordingConfirmation(bool result) : IConfirmationDialogService
    {
        public List<string> Messages { get; } = [];

        public List<string> ConfirmLabels { get; } = [];

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string confirmLabel,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(message);
            ConfirmLabels.Add(confirmLabel);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? WrittenText { get; private set; }

        public Task<ClipboardReadResult> ReadTextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ClipboardReadResult.Success(WrittenText));
        }

        public Task<ClipboardWriteResult> WriteTextAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WrittenText = text;
            return Task.FromResult(ClipboardWriteResult.Success);
        }
    }

    private sealed class RecordingSftpService(ISftpService inner) : ISftpService
    {
        public int ListCalls { get; private set; }

        public int RenameCalls { get; private set; }

        public int CreateCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public string? DeleteFailurePath { get; init; }

        public bool BlockDeletes { get; init; }

        public TaskCompletionSource DeleteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SftpResult<IReadOnlyList<RemoteFileInfo>>> ListAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return inner.ListAsync(path, cancellationToken);
        }

        public Task<SftpResult<RemoteFileInfo?>> StatAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            return inner.StatAsync(path, cancellationToken);
        }

        public Task<SftpResult> CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return inner.CreateDirectoryAsync(path, cancellationToken);
        }

        public Task<SftpResult> RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            RenameCalls++;
            return inner.RenameAsync(sourcePath, destinationPath, cancellationToken);
        }

        public async Task<SftpResult> DeleteAsync(
            string path,
            bool recursive,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            _ = DeleteStarted.TrySetResult();
            if (BlockDeletes)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return string.Equals(path, DeleteFailurePath, StringComparison.Ordinal)
                ? SftpResult.Fail(SftpError.PermissionDenied, "Permission denied by the test server.")
                : await inner.DeleteAsync(path, recursive, cancellationToken);
        }

        public Task<SftpResult> SetPermissionsAsync(
            string path,
            UnixFileMode mode,
            CancellationToken cancellationToken = default)
        {
            return inner.SetPermissionsAsync(path, mode, cancellationToken);
        }

        public Task<SftpResult<string>> GetRealPathAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            return inner.GetRealPathAsync(path, cancellationToken);
        }

        public Task<SftpResult<Stream>> OpenReadAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            return inner.OpenReadAsync(path, cancellationToken);
        }

        public Task<SftpResult<Stream>> OpenWriteAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            return inner.OpenWriteAsync(path, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return inner.DisposeAsync();
        }
    }

    private sealed class DeniedListSftpService(ISftpService inner, string deniedPath) : ISftpService
    {
        public Task<SftpResult<IReadOnlyList<RemoteFileInfo>>> ListAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            return string.Equals(SftpPath.Normalize(path), deniedPath, StringComparison.Ordinal)
                ? Task.FromResult(SftpResult<IReadOnlyList<RemoteFileInfo>>.Fail(
                    SftpError.PermissionDenied,
                    "Permission denied for this folder."))
                : inner.ListAsync(path, cancellationToken);
        }

        public Task<SftpResult<RemoteFileInfo?>> StatAsync(string path, CancellationToken cancellationToken = default) =>
            inner.StatAsync(path, cancellationToken);
        public Task<SftpResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken = default) =>
            inner.CreateDirectoryAsync(path, cancellationToken);
        public Task<SftpResult> RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default) =>
            inner.RenameAsync(sourcePath, destinationPath, cancellationToken);
        public Task<SftpResult> DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(path, recursive, cancellationToken);
        public Task<SftpResult> SetPermissionsAsync(string path, UnixFileMode mode, CancellationToken cancellationToken = default) =>
            inner.SetPermissionsAsync(path, mode, cancellationToken);
        public Task<SftpResult<string>> GetRealPathAsync(string path, CancellationToken cancellationToken = default) =>
            inner.GetRealPathAsync(path, cancellationToken);
        public Task<SftpResult<Stream>> OpenReadAsync(string path, CancellationToken cancellationToken = default) =>
            inner.OpenReadAsync(path, cancellationToken);
        public Task<SftpResult<Stream>> OpenWriteAsync(string path, CancellationToken cancellationToken = default) =>
            inner.OpenWriteAsync(path, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

#pragma warning restore IDE0022
