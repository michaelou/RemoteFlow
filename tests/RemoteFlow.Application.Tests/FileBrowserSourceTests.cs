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

            // Hidden means what the operating system means by it: the attribute on Windows, a leading dot
            // on Unix. .NET reports both through FileAttributes.Hidden, which is why the source can test
            // one flag and be right on both. Writing this the Windows way everywhere passes only on
            // Windows -- File.SetAttributes is a silent no-op off it, so the entry stays visible and the
            // assertion below fails for a reason that has nothing to do with the code under test.
            var hidden = Path.Combine(root, OperatingSystem.IsWindows() ? "hidden.txt" : ".hidden.txt");
            await File.WriteAllTextAsync(hidden, "x", token);
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
            }

            _ = Directory.CreateDirectory(Path.Combine(root, "nested"));
            var source = new LocalFileBrowserSource();

            var listed = await source.ListAsync(root, cancellationToken: token);
            var withHidden = await source.ListAsync(
                root,
                new FileBrowserListOptions { ShowHidden = true },
                token);

            // Whichever way the entry was hidden, the source reports the same two entries: it asks
            // FileAttributes.Hidden and lets the platform decide what that means.
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

    /// <summary>The move behind a drag from the SFTP page's remote list onto its local pane: the rows are
    /// already staged on disk, so finishing the drop must not download them a second time.</summary>
    [Fact]
    public async Task MovingIntoAFolderRelocatesFilesAndTreesAndRefusesToClobber()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
            var destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            await File.WriteAllTextAsync(Path.Combine(staging, "report.txt"), "report", token);
            var tree = Directory.CreateDirectory(Path.Combine(staging, "tree")).FullName;
            _ = Directory.CreateDirectory(Path.Combine(tree, "nested"));
            await File.WriteAllTextAsync(Path.Combine(tree, "nested", "child.txt"), "child", token);
            var source = new LocalFileBrowserSource();

            var file = await source.MoveIntoAsync(Path.Combine(staging, "report.txt"), destination, token);
            var directory = await source.MoveIntoAsync(tree, destination, token);

            Assert.True(file.IsSuccess);
            Assert.Equal(Path.Combine(destination, "report.txt"), file.Value);
            Assert.Equal("report", await File.ReadAllTextAsync(file.Value, token));
            Assert.False(File.Exists(Path.Combine(staging, "report.txt")));
            Assert.True(directory.IsSuccess);
            Assert.Equal(
                "child",
                await File.ReadAllTextAsync(Path.Combine(destination, "tree", "nested", "child.txt"), token));
            Assert.False(Directory.Exists(tree));

            // A silent overwrite of a local file the user never named is not something a drop may do.
            await File.WriteAllTextAsync(Path.Combine(staging, "report.txt"), "second", token);
            var clash = await source.MoveIntoAsync(Path.Combine(staging, "report.txt"), destination, token);
            Assert.Equal(SftpError.AlreadyExists, clash.Failure.Error);
            Assert.Equal("report", await File.ReadAllTextAsync(file.Value, token));

            var missing = await source.MoveIntoAsync(Path.Combine(staging, "gone.txt"), destination, token);
            Assert.Equal(SftpError.NotFound, missing.Failure.Error);

            var nowhere = await source.MoveIntoAsync(
                Path.Combine(staging, "report.txt"),
                Path.Combine(root, "not-a-folder"),
                token);
            Assert.Equal(SftpError.InvalidPath, nowhere.Failure.Error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A path dragged onto the window arrives as a path and nothing else, so it has to be
    /// described before it can be transferred like a row the pane listed itself.</summary>
    [Fact]
    public async Task ADraggedInPathIsDescribedWhateverItIsAndHiddenOrMissingOnesAreNotHidden()
    {
        var token = TestContext.Current.CancellationToken;
        var root = CreateTempDirectory();
        try
        {
            var file = Path.Combine(root, "report.txt");
            await File.WriteAllTextAsync(file, "seven bytes", token);
            var folder = Path.Combine(root, "photos");
            _ = Directory.CreateDirectory(folder);
            var hidden = Path.Combine(root, ".env");
            await File.WriteAllTextAsync(hidden, "secret", token);
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(hidden, FileAttributes.Hidden);
            }

            var described = LocalFileBrowserSource.TryDescribe(file);
            Assert.NotNull(described);
            Assert.Equal("report.txt", described.Name);
            Assert.Equal(file, described.Path);
            Assert.False(described.IsDirectory);
            Assert.Equal(11, described.Size);

            var directory = LocalFileBrowserSource.TryDescribe(folder);
            Assert.NotNull(directory);
            Assert.Equal("photos", directory.Name);
            Assert.True(directory.IsDirectory);

            // A trailing separator is not a different folder, and the name is still the last segment.
            var trailing = LocalFileBrowserSource.TryDescribe(folder + Path.DirectorySeparatorChar);
            Assert.NotNull(trailing);
            Assert.Equal("photos", trailing.Name);
            Assert.Equal(folder, trailing.Path);

            // The pane filters hidden entries so it is not full of noise nobody asked for. Something
            // dragged on by hand was asked for, and dropping it silently would look like a failed drop.
            Assert.NotNull(LocalFileBrowserSource.TryDescribe(hidden));

            Assert.Null(LocalFileBrowserSource.TryDescribe(Path.Combine(root, "gone.txt")));
            Assert.Null(LocalFileBrowserSource.TryDescribe(string.Empty));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "remoteflow-browser-" + Path.GetRandomFileName());
        _ = Directory.CreateDirectory(path);
        return path;
    }
}
