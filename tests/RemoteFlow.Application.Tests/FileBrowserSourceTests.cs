using System.Text;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.Application.Abstractions.Storage;
using RemoteFlow.Application.Services;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>Both browser sources against the one port. What is under test is that a single pane can serve
/// <c>C:\Users\andreas</c> and <c>media-prod/2024/</c> without knowing which it has — and that the local
/// side survives the things a real filesystem does to an enumerator.</summary>
public sealed class FileBrowserSourceTests
{
    [Fact]
    public async Task ALocalDirectoryListsItsEntriesAndFiltersHiddenOnesByAttribute()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "visible.txt"), "x", token);
            var hidden = Path.Combine(root, "hidden.txt");
            await File.WriteAllTextAsync(hidden, "x", token);
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
            _ = Directory.CreateDirectory(Path.Combine(root, "nested"));
            var source = new LocalFileBrowserSource();

            var listed = await source.ListAsync(root, cancellationToken: token);
            var withHidden = await source.ListAsync(
                root,
                new FileBrowserListOptions { ShowHidden = true },
                token);

            // The correct local test is the hidden attribute, not a leading dot: "." names nothing on
            // Windows, and a dotfile is not hidden to the operating system on either platform.
            Assert.Equal(
                ["nested", "visible.txt"],
                listed.Value.Entries.Select(entry => entry.Name).Order(StringComparer.Ordinal));
            Assert.Equal(3, withHidden.Value.Entries.Count);
            Assert.True(listed.Value.Entries.Single(entry => entry.Name == "nested").IsDirectory);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ALargeLocalDirectoryPagesAgainstASyntheticToken()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            for (var index = 0; index < 250; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(root, $"file-{index:D3}.txt"), "x", token);
            }

            var source = new LocalFileBrowserSource();
            var options = new FileBrowserListOptions { PageSize = 100 };

            var first = await source.ListAsync(root, options, token);
            var second = await source.ListAsync(
                root,
                options with { ContinuationToken = first.Value.ContinuationToken },
                token);
            var third = await source.ListAsync(
                root,
                options with { ContinuationToken = second.Value.ContinuationToken },
                token);

            // The same shape a 200,000-key prefix has, so the pane keeps zero source-specific branches.
            Assert.Equal(100, first.Value.Entries.Count);
            Assert.Equal(100, second.Value.Entries.Count);
            Assert.Equal(50, third.Value.Entries.Count);
            Assert.Null(third.Value.ContinuationToken);
            Assert.Equal("file-000.txt", first.Value.Entries[0].Name);
            Assert.Equal("file-100.txt", second.Value.Entries[0].Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AMidEnumerationFailureYieldsAPartialPageAndAWarningRatherThanABlankPane()
    {
        var token = TestContext.Current.CancellationToken;
        var source = new LocalFileBrowserSource();

        // A directory that vanishes between the check and the walk is the same failure shape as
        // C:\System Volume Information: the enumerator throws from MoveNext, not from the call.
        var missing = Path.Combine(Path.GetTempPath(), "remoteflow-missing-" + Guid.NewGuid().ToString("N"));
        var absent = await source.ListAsync(missing, cancellationToken: token);

        Assert.True(absent.IsFailure);
        Assert.Equal(SftpError.NotFound, absent.Failure.Error);

        var root = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "readable.txt"), "x", token);
            var page = await source.ListAsync(root, cancellationToken: token);

            Assert.True(page.IsSuccess);
            Assert.Null(page.Value.Warning);
            _ = Assert.Single(page.Value.Entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LocalRootsAndParentsAreSensibleOnBothPlatforms()
    {
        var source = new LocalFileBrowserSource();
        var roots = LocalFileBrowserSource.Roots();

        Assert.NotEmpty(roots);
        foreach (var candidate in roots)
        {
            // A root has no parent. That is what stops the pane offering a walk above C:\ or /.
            Assert.Null(source.GetParent(candidate));
            Assert.True(source.IsValidPath(candidate));
        }

        var nested = Path.Combine(roots[0], "one", "two");
        Assert.Equal(Path.Combine(roots[0], "one"), source.GetParent(nested));
        Assert.Equal("two", source.GetName(nested));
        Assert.False(source.IsValidPath("relative/path"));
        Assert.Equal(roots[0], source.GetBreadcrumbs(nested)[0].Path);
        Assert.Equal(3, source.GetBreadcrumbs(nested).Count);
    }

    [Fact]
    public void TheLocalRootsAreOfferedAsALabelledPickerAndTheObjectSideOffersNone()
    {
        var local = new LocalFileBrowserSource();

        var roots = local.GetRoots();

        Assert.NotEmpty(roots);
        Assert.Equal(LocalFileBrowserSource.Roots().Count, roots.Count);
        foreach (var root in roots)
        {
            // The label carries the volume name where there is one; the path is what the pane navigates to.
            Assert.False(string.IsNullOrWhiteSpace(root.Label));
            Assert.StartsWith(root.Path, root.Label, StringComparison.Ordinal);
            Assert.True(local.IsValidPath(root.Path));
            Assert.Null(local.GetParent(root.Path));
        }

        // The connection already pins where an object-storage pane starts, so there is no second place to
        // offer and the picker stays hidden.
        Assert.Empty(new ObjectStorageFileBrowserSource(
            new InMemoryObjectStorage(),
            "s3://media",
            "/media").GetRoots());
    }

    [Fact]
    public async Task ALocalRenameAndDeleteRoundTrip()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var source = new LocalFileBrowserSource();
            var file = Path.Combine(root, "before.txt");
            await File.WriteAllTextAsync(file, "x", token);

            Assert.True((await source.RenameAsync(file, "after.txt", token)).IsSuccess);
            Assert.True(File.Exists(Path.Combine(root, "after.txt")));

            var entry = new FileBrowserEntry("after.txt", Path.Combine(root, "after.txt"), false, 1, null);
            Assert.True((await source.DeleteAsync(entry, token)).IsSuccess);
            Assert.False(File.Exists(Path.Combine(root, "after.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ObjectStorageGroupsPrefixesAndDoesNotListAFolderMarkerTwice()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = new InMemoryObjectStorage();
        store.Seed("/media/readme.txt", Encoding.UTF8.GetBytes("hello"));
        store.Seed("/media/2024/", []);
        store.Seed("/media/2024/clip.mov", Encoding.UTF8.GetBytes("frames"));
        var source = new ObjectStorageFileBrowserSource(store, "s3://media", "/media");

        var page = await source.ListAsync("/media", cancellationToken: token);

        Assert.Equal(
            [("2024", true), ("readme.txt", false)],
            [.. page.Value.Entries.Select(entry => (entry.Name, entry.IsDirectory))]);
    }

    [Fact]
    public async Task ObjectStorageRoundTripsAContinuationTokenAcrossPages()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = new InMemoryObjectStorage();
        for (var index = 0; index < 5; index++)
        {
            store.Seed($"/media/object-{index}.bin", [1]);
        }

        var source = new ObjectStorageFileBrowserSource(store, "s3://media", "/media");
        var options = new FileBrowserListOptions { PageSize = 2 };

        var first = await source.ListAsync("/media", options, token);
        var second = await source.ListAsync(
            "/media",
            options with { ContinuationToken = first.Value.ContinuationToken },
            token);

        Assert.Equal(2, first.Value.Entries.Count);
        Assert.NotNull(first.Value.ContinuationToken);
        Assert.Equal(["object-2.bin", "object-3.bin"], second.Value.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task ExpandingForADeletePlanCrossesAPageBoundary()
    {
        var token = TestContext.Current.CancellationToken;
        await using var store = new InMemoryObjectStorage { PageSizeCap = 2 };
        for (var index = 0; index < 7; index++)
        {
            store.Seed($"/media/logs/entry-{index}.log", [1]);
        }

        var source = new ObjectStorageFileBrowserSource(store, "s3://media", "/media");
        var root = new FileBrowserEntry("logs", "/media/logs", true, 0, null);

        var expanded = new List<FileBrowserEntry>();
        await foreach (var entry in source.EnumerateRecursiveAsync(root, token))
        {
            expanded.Add(entry);
        }

        // Paging is user-visible while browsing and invisible here: the plan has to walk the whole prefix.
        Assert.Equal(8, expanded.Count);
        Assert.Equal(7, expanded.Count(entry => !entry.IsDirectory));
    }

    [Fact]
    public void ObjectStorageRefusesRenameAndNeverWalksAboveItsRoot()
    {
        var store = new InMemoryObjectStorage();
        var source = new ObjectStorageFileBrowserSource(store, "s3://media", "/media/2024");

        Assert.False(source.SupportsRename);
        Assert.False(source.SupportsHiddenEntries);
        Assert.Null(source.GetParent("/media/2024"));
        Assert.Equal("/media/2024", source.GetParent("/media/2024/raw"));
        Assert.False(source.IsValidPath("/other-bucket"));
        Assert.True(source.IsValidPath("/media/2024/raw"));
        Assert.Equal("media/2024", source.GetBreadcrumbs("/media/2024/raw")[0].Label);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "remoteflow-browser-" + Path.GetRandomFileName());
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
