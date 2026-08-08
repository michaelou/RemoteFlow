using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Services;
using RemoteFlow.TestSupport;
using Xunit;

#pragma warning disable IDE0022 // Compact forwarding members keep this fault-injection double readable.

namespace RemoteFlow.Application.Tests;

public sealed class SftpPublisherTests
{
    [Fact]
    public async Task PublishReplacesAnExistingFileKeepsItsModeAndLeavesNothingBehind()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        var mode = (UnixFileMode)Convert.ToInt32("0755", 8);
        await WriteAsync(sftp, "/srv/app/run.sh", "old", token);
        Assert.True((await sftp.SetPermissionsAsync("/srv/app/run.sh", mode, token)).IsSuccess);
        await WriteAsync(sftp, "/srv/app/run.sh.part", "new", token);

        var published = await SftpPublisher.PublishAsync(sftp, "/srv/app/run.sh.part", "/srv/app/run.sh", token);

        Assert.True(published.IsSuccess);
        Assert.Equal("new", await ReadAsync(sftp, "/srv/app/run.sh", token));
        Assert.Equal(mode, (await sftp.StatAsync("/srv/app/run.sh", token)).Value!.Mode);
        var listed = await sftp.ListAsync("/srv/app", token);
        Assert.Equal("run.sh", Assert.Single(listed.Value).Name);
    }

    [Fact]
    public async Task PublishToAFreeNameIsASingleRename()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        await WriteAsync(inner, "/srv/app/fresh.txt.part", "new", token);
        var sftp = new ScriptedRenameSftpService(inner, (_, _) => null);

        var published = await SftpPublisher.PublishAsync(sftp, "/srv/app/fresh.txt.part", "/srv/app/fresh.txt", token);

        Assert.True(published.IsSuccess);
        Assert.Equal(1, sftp.RenameCalls);
        Assert.Equal("new", await ReadAsync(inner, "/srv/app/fresh.txt", token));
    }

    [Fact]
    public async Task AFailedPublishPutsThePreviousFileBackUnderItsOwnName()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        await WriteAsync(inner, "/srv/app/config.yaml", "trusted", token);
        await WriteAsync(inner, "/srv/app/config.yaml.part", "half-baked", token);
        var sftp = new ScriptedRenameSftpService(
            inner,
            (source, _) => source.EndsWith(".part", StringComparison.Ordinal)
                ? SftpResult.Fail(SftpError.QuotaExceeded, "The scripted server rejected the publish.")
                : null);

        var published = await SftpPublisher.PublishAsync(sftp, "/srv/app/config.yaml.part", "/srv/app/config.yaml", token);

        Assert.True(published.IsFailure);
        Assert.Equal(SftpError.QuotaExceeded, published.Failure.Error);
        Assert.Equal("trusted", await ReadAsync(inner, "/srv/app/config.yaml", token));
        var listed = await inner.ListAsync("/srv/app", token);
        Assert.Equal(
            ["config.yaml", "config.yaml.part"],
            listed.Value.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task APublishOntoAFileThatCannotBeMovedAsideReportsTheRenameFailure()
    {
        var token = TestContext.Current.CancellationToken;
        var inner = new FakeSftpService();
        await WriteAsync(inner, "/srv/app/locked.conf", "trusted", token);
        await WriteAsync(inner, "/srv/app/locked.conf.part", "new", token);
        var sftp = new ScriptedRenameSftpService(
            inner,
            (_, _) => SftpResult.Fail(SftpError.PermissionDenied, "The scripted server refused the rename."));

        var published = await SftpPublisher.PublishAsync(sftp, "/srv/app/locked.conf.part", "/srv/app/locked.conf", token);

        Assert.True(published.IsFailure);
        Assert.Equal(SftpError.PermissionDenied, published.Failure.Error);
        Assert.Equal("trusted", await ReadAsync(inner, "/srv/app/locked.conf", token));
    }

    private static async Task WriteAsync(
        FakeSftpService sftp,
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        var opened = await sftp.OpenWriteAsync(path, cancellationToken);
        Assert.True(opened.IsSuccess);
        await using var stream = opened.Value;
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(contents.AsMemory(), cancellationToken);
    }

    private static async Task<string> ReadAsync(
        FakeSftpService sftp,
        string path,
        CancellationToken cancellationToken)
    {
        var opened = await sftp.OpenReadAsync(path, cancellationToken);
        Assert.True(opened.IsSuccess);
        await using var stream = opened.Value;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>A server whose renames can be failed on demand, by source and destination path.</summary>
    private sealed class ScriptedRenameSftpService(
        ISftpService inner,
        Func<string, string, SftpResult?> rename) : ISftpService
    {
        public int RenameCalls { get; private set; }

        public Task<SftpResult> RenameAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken = default)
        {
            RenameCalls++;
            var scripted = rename(sourcePath, destinationPath);
            return scripted is null
                ? inner.RenameAsync(sourcePath, destinationPath, cancellationToken)
                : Task.FromResult(scripted);
        }

        public Task<SftpResult<IReadOnlyList<RemoteFileInfo>>> ListAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.ListAsync(path, cancellationToken);

        public Task<SftpResult<RemoteFileInfo?>> StatAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.StatAsync(path, cancellationToken);

        public Task<SftpResult> CreateDirectoryAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.CreateDirectoryAsync(path, cancellationToken);

        public Task<SftpResult> DeleteAsync(
            string path,
            bool recursive,
            CancellationToken cancellationToken = default) => inner.DeleteAsync(path, recursive, cancellationToken);

        public Task<SftpResult> SetPermissionsAsync(
            string path,
            UnixFileMode mode,
            CancellationToken cancellationToken = default) => inner.SetPermissionsAsync(path, mode, cancellationToken);

        public Task<SftpResult<string>> GetRealPathAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.GetRealPathAsync(path, cancellationToken);

        public Task<SftpResult<Stream>> OpenReadAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.OpenReadAsync(path, cancellationToken);

        public Task<SftpResult<Stream>> OpenWriteAsync(
            string path,
            CancellationToken cancellationToken = default) => inner.OpenWriteAsync(path, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

#pragma warning restore IDE0022
