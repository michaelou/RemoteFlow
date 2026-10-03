using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Services.Docker;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.ViewModels.Docker;
using RemoteFlow.UI.Views.Docker;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>The Compose, Images and Volumes tabs.</summary>
public sealed partial class DockerWorkspaceTests
{
    [Fact]
    public async Task ComposeListsLiveProjectsAndRemembersOnesBroughtDown()
    {
        var token = TestContext.Current.CancellationToken;
        using var memory = new SettingsDockerComposeProjectMemory(new InMemorySettingsStore());
        var fixture = CreateFixture(composeMemory: memory);
        fixture.Host.ProjectList = [new DockerComposeProject("shop", "running(2)", ["/srv/shop/compose.yaml"])];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        await fixture.ViewModel.RefreshAsync(token);
        var shop = Assert.Single(fixture.ViewModel.ComposeProjects);
        Assert.True(shop.IsRunning);
        Assert.True(shop.CanDown);
        Assert.Equal("1 project, 1 running", fixture.ViewModel.Summary);

        fixture.Host.ProjectList = [];
        await fixture.ViewModel.RefreshAsync(token);

        Assert.Same(shop, Assert.Single(fixture.ViewModel.ComposeProjects));
        Assert.Equal("down", shop.StatusText);
        Assert.True(shop.CanUp);
        Assert.False(shop.CanDown);
        Assert.True(shop.CanForget);
    }

    [Fact]
    public async Task BringingAProjectUpUsesItsRememberedFilesAndRefreshesEverything()
    {
        var token = TestContext.Current.CancellationToken;
        using var memory = new SettingsDockerComposeProjectMemory(new InMemorySettingsStore());
        var fixture = CreateFixture(EnvironmentKind.Development, composeMemory: memory);
        await memory.RememberAsync(
            fixture.Connection.Id,
            [new DockerComposeProject("shop", "running(2)", ["/srv/shop/compose.yaml", "/srv/shop/prod.yaml"])],
            token);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        await fixture.ViewModel.RefreshAsync(token);
        var shop = Assert.Single(fixture.ViewModel.ComposeProjects);
        fixture.Host.OperationOutput = _ =>
        {
            fixture.Host.ProjectList = [new DockerComposeProject("shop", "running(2)", ["/srv/shop/compose.yaml", "/srv/shop/prod.yaml"])];
            fixture.Host.Containers = [Container("c1", "shop-api-1", DockerContainerState.Running, project: "shop")];
            return FakeSshRunningCommand.Completed(0, " Container shop-api-1  Started");
        };

        await fixture.ViewModel.ComposeUpCommand.ExecuteAsync(shop);

        var up = Assert.IsType<DockerOperation.ComposeUp>(Assert.Single(fixture.Host.Operations));
        Assert.Equal("shop", up.Project);
        Assert.Equal(["/srv/shop/compose.yaml", "/srv/shop/prod.yaml"], up.ConfigFiles);
        Assert.Empty(fixture.Confirmation.Messages);
        Assert.True(shop.IsRunning);
        Assert.Equal("shop-api-1", Assert.Single(fixture.ViewModel.Containers).Name);
        Assert.Equal("Compose up — shop", fixture.ViewModel.Logs.Heading);
        Assert.Equal("Finished.", fixture.ViewModel.Logs.StatusMessage);
        Assert.Equal(" Container shop-api-1  Started", Assert.Single(fixture.ViewModel.Logs.Lines).Text);
    }

    [Fact]
    public async Task BringingAProjectDownAlwaysAsksAndDeletingVolumesSaysSo()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(EnvironmentKind.Development, confirmations: [false, true]);
        fixture.Host.ProjectList = [new DockerComposeProject("shop", "running(2)", ["/srv/shop/compose.yaml"])];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        await fixture.ViewModel.RefreshAsync(token);
        var shop = fixture.ViewModel.ComposeProjects[0];

        await fixture.ViewModel.ComposeDownCommand.ExecuteAsync(shop);
        Assert.Empty(fixture.Host.Operations);

        await fixture.ViewModel.ComposeDownWithVolumesCommand.ExecuteAsync(shop);

        Assert.Equal(new DockerOperation.ComposeDown("shop", RemoveVolumes: true), Assert.Single(fixture.Host.Operations));
        Assert.Contains("delete its named volumes", fixture.Confirmation.Messages[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedOperationIsReportedAndTheOutputStaysInThePane()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(EnvironmentKind.Development);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.PullReference = "nginx:9.99";
        fixture.Host.OperationOutput = _ => FakeSshRunningCommand.Completed(1);

        await fixture.ViewModel.PullImageCommand.ExecuteAsync(null);

        Assert.Equal(new DockerOperation.PullImage("nginx:9.99"), Assert.Single(fixture.Host.Operations));
        Assert.Equal("Pull — nginx:9.99: Failed: exited with code 1. The output is in the pane below.", fixture.ViewModel.ErrorMessage);
        Assert.Equal("nginx:9.99", fixture.ViewModel.PullReference);
        Assert.True(fixture.ViewModel.Logs.IsOpen);
        Assert.True(fixture.ViewModel.Logs.CloseCommand.CanExecute(null));
    }

    [Fact]
    public async Task WhileAnOperationRunsThePaneCannotBeClosedOrTakenOverByLogs()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(EnvironmentKind.Development);
        fixture.Host.Containers = [Container("c1", "api", DockerContainerState.Running)];
        fixture.Host.ProjectList = [new DockerComposeProject("shop", "running(1)", ["/srv/shop/compose.yaml"])];
        var running = new FakeSshRunningCommand();
        fixture.Host.OperationOutput = _ => running;
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        await fixture.ViewModel.RefreshAsync(token);

        var up = fixture.ViewModel.ComposeUpCommand.ExecuteAsync(fixture.ViewModel.ComposeProjects[0]);
        await WaitUntilAsync(() => fixture.ViewModel.Logs.IsOperationRunning, token);

        Assert.False(fixture.ViewModel.Logs.CloseCommand.CanExecute(null));
        Assert.True(fixture.ViewModel.ComposeProjects[0].IsBusy);
        await fixture.ViewModel.ShowLogsCommand.ExecuteAsync(fixture.ViewModel.Containers[0]);
        Assert.Equal("Wait for 'Compose up — shop' to finish before opening logs.", fixture.ViewModel.ErrorMessage);
        Assert.Empty(fixture.Host.LogRequests);
        Assert.False(running.IsDisposed);

        running.Publish("Pulling api");
        running.Complete(0);
        await up;

        Assert.False(fixture.ViewModel.Logs.IsOperationRunning);
        Assert.False(fixture.ViewModel.ComposeProjects[0].IsBusy);
        Assert.True(fixture.ViewModel.Logs.CloseCommand.CanExecute(null));
    }

    [Fact]
    public async Task ComposeUpFromAFileLetsComposeNameTheProject()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(EnvironmentKind.Development);
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        Assert.False(fixture.ViewModel.ComposeUpFromFileCommand.CanExecute(null));
        fixture.ViewModel.ComposeFilePath = " /srv/new app/compose.yaml ";
        await fixture.ViewModel.ComposeUpFromFileCommand.ExecuteAsync(null);

        Assert.Equal(
            new DockerOperation.ComposeUp(null, ["/srv/new app/compose.yaml"]),
            Assert.Single(fixture.Host.Operations),
            new ComposeUpComparer());
        Assert.Equal(string.Empty, fixture.ViewModel.ComposeFilePath);
    }

    [Fact]
    public async Task AMissingComposePluginIsTheTabsMessageNotThePagesError()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.ComposeFailure = new DockerFailure(DockerError.ComposeNotInstalled, "The Docker Compose plugin (docker compose) is not installed on this server.");
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        await fixture.ViewModel.RefreshAsync(token);

        Assert.Null(fixture.ViewModel.ErrorMessage);
        Assert.Contains("not installed", fixture.ViewModel.ComposeMessage, StringComparison.Ordinal);
        Assert.False(fixture.ViewModel.HasNoComposeProjects);
    }

    [Fact]
    public async Task ImagesKnowWhichContainersUseThemAndOnlyUnusedOnesCanBeRemoved()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(confirmations: [true]);
        fixture.Host.Containers =
        [
            Container("c1", "web", DockerContainerState.Exited, image: "nginx:1.27"),
            Container("c2", "tool", DockerContainerState.Running, image: "busybox"),
        ];
        fixture.Host.ImageList =
        [
            new DockerImage("aaaaaaaaaaaa", "nginx", "1.27", "141MB", "2 days ago", null),
            new DockerImage("bbbbbbbbbbbb", "busybox", "latest", "4MB", "1 week ago", null),
            new DockerImage("cccccccccccc", "redis", "7", "117MB", "1 month ago", 0),
            new DockerImage("dddddddddddd", null, null, "80MB", "1 month ago", null),
        ];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        fixture.ViewModel.SelectedTab = DockerTab.Images;
        await fixture.ViewModel.RefreshAsync(token);
        var images = fixture.ViewModel.Images;

        Assert.Equal(["busybox", "nginx", "redis", "<none>"], images.Select(image => image.Repository));
        Assert.Equal("tool", images[0].UsedByText);
        Assert.Equal("web", images[1].UsedByText);
        Assert.False(images[1].CanRemove);
        Assert.True(images[2].CanRemove);
        Assert.Equal("4 images, 2 unused", fixture.ViewModel.Summary);

        await fixture.ViewModel.RemoveImageCommand.ExecuteAsync(images[1]);
        Assert.Empty(fixture.Host.RemovedImages);

        await fixture.ViewModel.RemoveImageCommand.ExecuteAsync(images[3]);
        Assert.Equal(["dddddddddddd"], fixture.Host.RemovedImages);
        Assert.Equal(3, fixture.ViewModel.Images.Count);
    }

    [Fact]
    public async Task VolumesKnowWhichContainersMountThemAndDeletingAsks()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(confirmations: [false, true]);
        fixture.Host.Containers =
        [
            Container("c1", "db", DockerContainerState.Exited, volumes: ["shop_pgdata"]),
        ];
        fixture.Host.VolumeList =
        [
            new DockerVolume("shop_pgdata", "local", "shop"),
            new DockerVolume("old_cache", "local", null),
        ];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        fixture.ViewModel.SelectedTab = DockerTab.Volumes;
        await fixture.ViewModel.RefreshAsync(token);
        var volumes = fixture.ViewModel.Volumes;

        Assert.Equal("db", volumes[0].UsedByText);
        Assert.False(volumes[0].CanRemove);
        Assert.True(volumes[1].CanRemove);

        await fixture.ViewModel.RemoveVolumeCommand.ExecuteAsync(volumes[1]);
        Assert.Empty(fixture.Host.RemovedVolumes);
        await fixture.ViewModel.RemoveVolumeCommand.ExecuteAsync(volumes[1]);

        Assert.Equal(["old_cache"], fixture.Host.RemovedVolumes);
        Assert.Contains("cannot be undone", fixture.Confirmation.Messages[0], StringComparison.Ordinal);
        Assert.Equal("shop_pgdata", Assert.Single(fixture.ViewModel.Volumes).Name);
    }

    [Fact]
    public async Task TheFilterAppliesToTheTabOnScreen()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.VolumeList = [new DockerVolume("shop_pgdata", "local", "shop"), new DockerVolume("cache", "local", null)];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.SelectedTab = DockerTab.Volumes;
        await fixture.ViewModel.RefreshAsync(token);

        fixture.ViewModel.FilterText = "shop";

        Assert.Equal("shop_pgdata", Assert.Single(fixture.ViewModel.Volumes).Name);
        Assert.Equal("Filter by name or project", fixture.ViewModel.FilterPlaceholder);
    }

    [AvaloniaFact]
    public async Task EveryTabRendersWithColumnsThatLineUp()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers = [Container("c1", "api", DockerContainerState.Running, project: "shop", volumes: ["shop_data"])];
        fixture.Host.ProjectList = [new DockerComposeProject("shop", "running(1)", ["/srv/shop/compose.yaml"])];
        fixture.Host.ImageList = [new DockerImage("aaaaaaaaaaaa", "nginx", "1.27", "141MB", "2 days ago", 0)];
        fixture.Host.VolumeList = [new DockerVolume("shop_data", "local", "shop")];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var window = new Window
        {
            Width = 1300,
            Height = 700,
            Content = new DockerWorkspace { DataContext = fixture.ViewModel },
        };
        window.Show();

        foreach (var (tab, heading, cell) in new[]
                 {
                     (DockerTab.Compose, "Compose files", "/srv/shop/compose.yaml"),
                     (DockerTab.Images, "Tag", "1.27"),
                     (DockerTab.Volumes, "Used by", "api"),
                 })
        {
            fixture.ViewModel.SelectedTab = tab;
            await fixture.ViewModel.RefreshAsync(token);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            AssertSameLeft(window, heading, cell);
        }

        Assert.Equal(4, window.GetVisualDescendants().OfType<TabItem>().Count());
        window.Close();
        fixture.ViewModel.Deactivate();
    }

    private sealed class ComposeUpComparer : IEqualityComparer<DockerOperation>
    {
        public bool Equals(DockerOperation? x, DockerOperation? y)
        {
            return x is DockerOperation.ComposeUp left && y is DockerOperation.ComposeUp right
                && left.Project == right.Project
                && left.ConfigFiles.SequenceEqual(right.ConfigFiles);
        }

        public int GetHashCode(DockerOperation obj)
        {
            return 0;
        }
    }
}
