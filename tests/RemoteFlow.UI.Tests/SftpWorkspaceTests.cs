using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Sftp;
using RemoteFlow.UI.ViewModels.Transfers;
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
    public async Task WorkspaceTransfersFeedTheSharedTransferPanel()
    {
        var token = TestContext.Current.CancellationToken;
        using var transfers = new TransfersPageViewModel(
            new InlineDispatcher(),
            new NoOpRevealService());
        var fixture = CreateFixture(transferManager: transfers);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var localRoot = CreateTempDirectory();
        try
        {
            var first = Path.Combine(localRoot, "first.txt");
            var second = Path.Combine(localRoot, "second.txt");
            await File.WriteAllTextAsync(first, "first", token);
            await File.WriteAllTextAsync(second, "second", token);

            await fixture.ViewModel.UploadAsync([first, second], "/home/test", token);

            Assert.Equal(2, transfers.CompletedCount);
            Assert.Equal(2, transfers.Items.Count);
            Assert.All(transfers.Items, item => Assert.Equal(ManagedTransferStatus.Completed, item.Status));
            Assert.Equal("No active transfers", transfers.AggregateStatus);
        }
        finally
        {
            Directory.Delete(localRoot, recursive: true);
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

    [Fact]
    public void PermissionGridAndOctalStayInSyncIncludingSpecialBitsAndRejectInvalidInput()
    {
        var sftp = new FakeSftpService();
        var target = FileInfoFor("/mode.txt", (UnixFileMode)Convert.ToInt32("0755", 8));
        var editor = new SftpPermissionsEditorViewModel(
            target,
            false,
            sftp,
            new RecordingConfirmation(true),
            _ => Task.CompletedTask)
        {
            OctalText = "7754",
        };
        Assert.True(editor.SetUserId);
        Assert.True(editor.SetGroupId);
        Assert.True(editor.Sticky);
        Assert.True(editor.UserExecute);
        Assert.True(editor.GroupExecute);
        Assert.False(editor.OtherWrite);
        Assert.False(editor.OtherExecute);

        editor.OtherWrite = true;
        Assert.Equal("7756", editor.OctalText);
        var gridBeforeInvalid = (editor.UserRead, editor.GroupExecute, editor.OtherWrite, editor.Sticky);
        editor.OctalText = "89-no";
        Assert.NotNull(editor.ValidationMessage);
        Assert.Equal(gridBeforeInvalid, (editor.UserRead, editor.GroupExecute, editor.OtherWrite, editor.Sticky));
        Assert.False(editor.CanApply);
    }

    [Fact]
    public async Task RecursivePermissionsUseDistinctDirectoryAndFileModesAndContinueAfterFailures()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        _ = await inner.CreateDirectoryAsync("/home/test/tree", token);
        _ = await inner.CreateDirectoryAsync("/home/test/tree/nested", token);
        await SeedFileAsync(inner, "/home/test/tree/root.txt", [1], token);
        await SeedFileAsync(inner, "/home/test/tree/nested/blocked.txt", [2], token);
        var recording = new RecordingSftpService(inner)
        {
            PermissionFailurePath = "/home/test/tree/nested/blocked.txt",
        };
        var target = (await inner.StatAsync("/home/test/tree", token)).Value!;
        var refreshes = 0;
        var editor = new SftpPermissionsEditorViewModel(
            target,
            false,
            recording,
            new RecordingConfirmation(true),
            _ =>
            {
                refreshes++;
                return Task.CompletedTask;
            })
        {
            Recursive = true,
            OctalText = "0750",
            FileOctalText = "0640",
        };

        var applied = await editor.ApplyAsync(token);

        Assert.False(applied);
        Assert.Equal(4, recording.PermissionCalls);
        Assert.Equal((UnixFileMode)Convert.ToInt32("0750", 8), recording.PermissionModes["/home/test/tree"]);
        Assert.Equal((UnixFileMode)Convert.ToInt32("0750", 8), recording.PermissionModes["/home/test/tree/nested"]);
        Assert.Equal((UnixFileMode)Convert.ToInt32("0640", 8), recording.PermissionModes["/home/test/tree/root.txt"]);
        Assert.Equal("/home/test/tree", recording.PermissionAttempts[^1]);
        var failure = Assert.Single(editor.Failures);
        Assert.Equal("/home/test/tree/nested/blocked.txt", failure.Path);
        Assert.Contains("Applied 3 of 4", editor.ResultMessage, StringComparison.Ordinal);
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task CurrentDirectoryModeZeroWarnsBeforeSendingChmod()
    {
        var inner = new FakeSftpService();
        var recording = new RecordingSftpService(inner);
        var confirmation = new RecordingConfirmation(false);
        var editor = new SftpPermissionsEditorViewModel(
            FileInfoFor("/home/test", (UnixFileMode)Convert.ToInt32("0755", 8), isDirectory: true),
            true,
            recording,
            confirmation,
            _ => Task.CompletedTask)
        {
            OctalText = "0000",
        };

        var applied = await editor.ApplyAsync(TestContext.Current.CancellationToken);

        Assert.False(applied);
        Assert.Equal(0, recording.PermissionCalls);
        Assert.Contains("lock this workspace out", confirmation.Messages.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("self-lockout", editor.ResultMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsupportedChmodIsClearAndUnchangedModeRoundTrips()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        await SeedFileAsync(inner, "/home/test/file.txt", [1], token);
        var target = (await inner.StatAsync("/home/test/file.txt", token)).Value!;
        var unsupported = new RecordingSftpService(inner)
        {
            PermissionFailurePath = target.FullPath,
            PermissionFailureError = SftpError.NotSupported,
        };
        var unsupportedEditor = new SftpPermissionsEditorViewModel(
            target, false, unsupported, new RecordingConfirmation(true), _ => Task.CompletedTask);

        Assert.False(await unsupportedEditor.ApplyAsync(token));
        Assert.Contains("does not support chmod", unsupportedEditor.ResultMessage, StringComparison.OrdinalIgnoreCase);

        var roundTrip = new SftpPermissionsEditorViewModel(
            target, false, inner, new RecordingConfirmation(true), _ => Task.CompletedTask);
        Assert.True(await roundTrip.ApplyAsync(token));
        Assert.Equal(target.Mode, (await inner.StatAsync(target.FullPath, token)).Value!.Mode);
    }

    [Fact]
    public async Task QuickBrowsingNeverShowsTheProgressIndicatorButASlowLoadDoes()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        await SeedFileAsync(inner, "/home/test/file.txt", [1], token);
        var recording = new RecordingSftpService(inner);
        var fixture = CreateFixture(recording);
        fixture.ViewModel.BusyIndicatorDelay = TimeSpan.FromMilliseconds(200);
        var appearances = 0;
        fixture.ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SftpWorkspaceViewModel.IsBusyIndicatorVisible) &&
                fixture.ViewModel.IsBusyIndicatorVisible)
            {
                _ = Interlocked.Increment(ref appearances);
            }
        };

        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        Assert.Equal(0, Volatile.Read(ref appearances));
        Assert.False(fixture.ViewModel.IsBusyIndicatorVisible);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        recording.ListGate = gate;
        var slowLoad = fixture.ViewModel.RefreshAsync(token);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!fixture.ViewModel.IsBusyIndicatorVisible && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20, token);
        }
        var shownWhileLoading = fixture.ViewModel.IsBusyIndicatorVisible;
        gate.SetResult();
        await slowLoad;

        Assert.True(shownWhileLoading);
        Assert.Equal(1, Volatile.Read(ref appearances));
        Assert.False(fixture.ViewModel.IsBusyIndicatorVisible);
    }

    [AvaloniaFact]
    public void ViewUsesVirtualizingStackPanel()
    {
        var view = new SftpWorkspace();
        Assert.NotNull(view);
    }

    [AvaloniaFact]
    public async Task RightClickHitsTheWholeRowNotJustTheFileName()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        await SeedFileAsync(fixture.Sftp, "/home/test/app.conf", [1], token);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var window = new Window
        {
            Width = 1000,
            Height = 600,
            Content = new SftpWorkspace { DataContext = fixture.ViewModel },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var row = window.GetVisualDescendants()
            .OfType<Grid>()
            .First(grid => grid.DataContext is SftpItemViewModel && grid.ContextFlyout is not null);
        // Far right of the row, past the last column's text: before the row itself was hit-testable
        // this landed on the list box and the flyout never opened.
        Assert.True(row.Bounds.Width > 100, $"row was not laid out: {row.Bounds}");
        var edge = row.TranslatePoint(new Point(row.Bounds.Width - 2, row.Bounds.Height / 2), window);
        Assert.True(edge.HasValue);

        var hit = Assert.IsAssignableFrom<Control>(window.InputHitTest(edge!.Value));

        Assert.Same(fixture.ViewModel.Items[0], hit.DataContext);
        window.Close();
    }

    [Fact]
    public async Task RemoteEditSaveOutcomesReachTheStatusBar()
    {
        var token = TestContext.Current.CancellationToken;
        var edits = new StubRemoteEditFactory();
        var fixture = CreateFixture(remoteEdits: edits);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var service = Assert.IsType<StubRemoteEditService>(edits.Service);

        service.RaiseUpload(new RemoteEditUploadResult(
            "/home/test/app.conf",
            "/cache/app.conf",
            Succeeded: false,
            "Permission was denied by the remote server."));

        Assert.Contains("app.conf", fixture.ViewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Permission was denied", fixture.ViewModel.ErrorMessage, StringComparison.Ordinal);

        service.RaiseUpload(new RemoteEditUploadResult("/home/test/app.conf", "/cache/app.conf", Succeeded: true));

        Assert.Contains("Saved 'app.conf'", fixture.ViewModel.FeedbackMessage, StringComparison.Ordinal);
    }

    private static Fixture CreateFixture(
        ISftpService? service = null,
        bool confirmationResult = true,
        TransfersPageViewModel? transferManager = null,
        IRemoteEditServiceFactory? remoteEdits = null)
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
            new SftpWorkspaceViewModel(
                factory,
                new StubFilePicker(),
                confirmation,
                clipboard,
                remoteEdits,
                transferManager),
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

    private static RemoteFileInfo FileInfoFor(string path, UnixFileMode mode, bool isDirectory = false)
    {
        return new RemoteFileInfo(
            SftpPath.GetName(path),
            path,
            0,
            DateTimeOffset.UnixEpoch,
            mode,
            "1000",
            "1000",
            isDirectory,
            false,
            null);
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

    private sealed class StubRemoteEditFactory : IRemoteEditServiceFactory
    {
        public IRemoteEditService? Service { get; private set; }

        public IRemoteEditService Create(ISftpService sftp, Guid sessionId) => Service = new StubRemoteEditService();

        public Task SweepStaleFilesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubRemoteEditService : IRemoteEditService
    {
        public event EventHandler? ActiveEditsChanged;

        public event EventHandler<RemoteEditUploadResult>? UploadCompleted;

        public IReadOnlyList<RemoteEditHandle> ActiveEdits => [];

        public int ActiveCount => 0;

        public void RaiseUpload(RemoteEditUploadResult result) => UploadCompleted?.Invoke(this, result);

        public void RaiseActiveEditsChanged() => ActiveEditsChanged?.Invoke(this, EventArgs.Empty);

        public Task<RemoteEditHandle> OpenAsync(string remotePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CloseAsync(RemoteEditHandle edit, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> CloseAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoOpRevealService : IFileRevealService
    {
        public Task<FileRevealResult> RevealAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(FileRevealResult.Success);
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

        public string? PermissionFailurePath { get; init; }

        public SftpError PermissionFailureError { get; init; } = SftpError.PermissionDenied;

        public int PermissionCalls { get; private set; }

        public Dictionary<string, UnixFileMode> PermissionModes { get; } = new(StringComparer.Ordinal);

        public List<string> PermissionAttempts { get; } = [];

        public TaskCompletionSource DeleteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? ListGate { get; set; }

        public async Task<SftpResult<IReadOnlyList<RemoteFileInfo>>> ListAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            if (ListGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }
            return await inner.ListAsync(path, cancellationToken);
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
            cancellationToken.ThrowIfCancellationRequested();
            PermissionCalls++;
            PermissionAttempts.Add(path);
            if (string.Equals(path, PermissionFailurePath, StringComparison.Ordinal))
            {
                return Task.FromResult(SftpResult.Fail(
                    PermissionFailureError,
                    PermissionFailureError == SftpError.NotSupported
                        ? "chmod is not supported by this server."
                        : "Permission denied by the test server."));
            }
            PermissionModes[path] = mode;
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
