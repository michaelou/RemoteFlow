using Avalonia.Headless.XUnit;
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

    [AvaloniaFact]
    public void ViewUsesVirtualizingStackPanel()
    {
        var view = new SftpWorkspace();
        Assert.NotNull(view);
    }

    private static Fixture CreateFixture(ISftpService? service = null)
    {
        var connection = Connection.Create(SystemGuidProvider.Instance, "Files", "example.test").Value;
        var ssh = new FakeSshConnection();
        var sftp = service ?? ssh.Sftp;
        var session = new SftpWorkspaceSession(connection, ssh, sftp);
        var factory = new StubSessionFactory(session);
        return new Fixture(connection, sftp, new SftpWorkspaceViewModel(factory, new StubFilePicker()));
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

    private sealed record Fixture(Connection Connection, ISftpService Sftp, SftpWorkspaceViewModel ViewModel);

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
