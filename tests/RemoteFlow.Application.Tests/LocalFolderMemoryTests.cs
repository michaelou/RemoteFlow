using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Services;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>Where a local browser pane was last pointed. One value, shared by the Storage and SFTP pages,
/// so switching pages does not switch folders.</summary>
public sealed class LocalFolderMemoryTests
{
    [Fact]
    public async Task AFolderIsRememberedAndRecalledOnlyWhileItStillExists()
    {
        var token = TestContext.Current.CancellationToken;
        var settings = new InMemorySettingsStore();
        var memory = new SettingsLocalFolderMemory(settings);
        var folder = Path.Combine(Path.GetTempPath(), "remoteflow-memory-" + Path.GetRandomFileName());
        _ = Directory.CreateDirectory(folder);
        try
        {
            Assert.Null(await memory.RecallAsync(token));

            await memory.RememberAsync(folder, token);

            Assert.Equal(folder, await memory.RecallAsync(token));
            Assert.Equal(folder, await settings.Get(SettingKeys.LastLocalFolder, token));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        // An ejected stick or a deleted folder must not make the pane open on an error banner every launch
        // until someone notices the path box.
        Assert.Null(await memory.RecallAsync(token));
    }
}
