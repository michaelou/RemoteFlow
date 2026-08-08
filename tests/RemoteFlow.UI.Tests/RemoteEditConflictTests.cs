using Avalonia.Headless.XUnit;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Sftp;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Sftp;
using RemoteFlow.UI.Views.Sftp;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed class RemoteEditConflictTests
{
    private static readonly RemoteEditConflict _conflict = new(
        "/srv/app/settings.json",
        new RemoteSnapshot(
            128,
            new DateTimeOffset(2026, 8, 8, 10, 0, 0, TimeSpan.Zero),
            new string('a', 64)),
        new RemoteSnapshot(
            256,
            new DateTimeOffset(2026, 8, 8, 10, 1, 0, TimeSpan.Zero),
            new string('b', 64)));

    [Fact]
    public async Task ConfiguredDefaultIsHonouredWithoutPrompting()
    {
        var settings = new InMemorySettingsStore();
        await settings.Set(
            SettingKeys.RemoteEditConflictDefault,
            RemoteEditConflictDefault.KeepBoth,
            TestContext.Current.CancellationToken);
        var dialogs = new StubConflictDialogs(RemoteEditConflictResolution.Cancel);
        var resolver = new RemoteEditConflictResolver(settings, dialogs, new StubConfirmation(true));

        var result = await resolver.ResolveAsync(_conflict, TestContext.Current.CancellationToken);

        Assert.Equal(RemoteEditConflictResolution.KeepBoth, result);
        Assert.Equal(0, dialogs.Calls);
    }

    [Fact]
    public async Task PromptResolutionIsUsedAndDiscardRequiresConfirmation()
    {
        var settings = new InMemorySettingsStore();
        var dialogs = new StubConflictDialogs(RemoteEditConflictResolution.DiscardLocal);
        var confirmation = new StubConfirmation(false);
        var resolver = new RemoteEditConflictResolver(settings, dialogs, confirmation);

        var result = await resolver.ResolveAsync(_conflict, TestContext.Current.CancellationToken);

        Assert.Equal(RemoteEditConflictResolution.Cancel, result);
        Assert.Equal(1, dialogs.Calls);
        Assert.Equal(1, confirmation.Calls);
    }

    [AvaloniaFact]
    public void DialogViewModelShowsDownloadedAndCurrentSnapshotsSideBySide()
    {
        var viewModel = new RemoteEditConflictDialogViewModel(_conflict);
        var dialog = new ConflictDialog(viewModel);

        Assert.Equal("128 bytes", viewModel.Downloaded.Size);
        Assert.Equal("256 bytes", viewModel.Current.Size);
        Assert.NotEqual(viewModel.Downloaded.Modified, viewModel.Current.Modified);
        Assert.NotNull(dialog);
    }

    private sealed class StubConflictDialogs(RemoteEditConflictResolution result) :
        IRemoteEditConflictDialogService
    {
        public int Calls { get; private set; }

        public Task<RemoteEditConflictResolution> ShowAsync(
            RemoteEditConflict conflict,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class StubConfirmation(bool result) : IConfirmationDialogService
    {
        public int Calls { get; private set; }

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string confirmLabel,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }
}
