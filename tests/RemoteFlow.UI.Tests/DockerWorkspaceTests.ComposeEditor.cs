using System.Text;
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

/// <summary>Container addresses and ports, and the compose editor.</summary>
public sealed partial class DockerWorkspaceTests
{
    [Fact]
    public async Task ContainersShowTheirAddressAndPublishedPortsAndFilterByThem()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers =
        [
            Container("c1", "api", DockerContainerState.Running,
                ports: "0.0.0.0:8080->80/tcp, [::]:8080->80/tcp",
                networks: [new DockerContainerNetwork("shop_default", "172.18.0.4", null)]),
            Container("c2", "agent", DockerContainerState.Running, networks: [new DockerContainerNetwork("host", null, null)]),
            Container("c3", "job", DockerContainerState.Exited),
        ];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var rows = fixture.ViewModel.Containers.ToDictionary(item => item.Name);

        Assert.Equal("172.18.0.4", rows["api"].AddressText);
        Assert.Equal("shop_default: 172.18.0.4", rows["api"].AddressTip);
        Assert.Equal("8080→80/tcp", rows["api"].PortsText);
        Assert.Equal("host", rows["agent"].AddressText);
        Assert.False(rows["agent"].HasAddress);
        Assert.Equal("—", rows["job"].AddressText);
        Assert.Equal("—", rows["job"].PortsText);

        fixture.ViewModel.FilterText = "172.18";
        Assert.Equal(["api"], fixture.ViewModel.Containers.Select(item => item.Name));
        fixture.ViewModel.FilterText = "8080";
        Assert.Equal(["api"], fixture.ViewModel.Containers.Select(item => item.Name));
    }

    [Fact]
    public async Task AProjectsComposeFileIsEditedSavedAndChecked()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        await SeedFileAsync(sftp, "/srv/shop/compose.yaml", "services:\r\n  web:\r\n    image: nginx:1.26\r\n", token);
        var fixture = CreateFixture(environment: EnvironmentKind.Development, composeFiles: new SftpDockerComposeFiles(sftp));
        fixture.Host.ProjectList = [new DockerComposeProject("shop", "running(1)", ["/srv/shop/compose.yaml"])];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        await fixture.ViewModel.RefreshAsync(token);
        var editor = fixture.ViewModel.ComposeEditor;

        await fixture.ViewModel.EditComposeFileCommand.ExecuteAsync(fixture.ViewModel.ComposeProjects[0]);
        Assert.True(editor.IsOpen);
        Assert.False(editor.IsDirty);
        Assert.Equal("Compose file of shop", editor.Heading);

        // The editor types "\n"; the file is written back in its own CRLF endings.
        editor.Text = editor.Text.Replace("nginx:1.26", "nginx:1.27\n    restart: always", StringComparison.Ordinal);
        Assert.True(editor.IsDirty);
        await fixture.ViewModel.SaveComposeFileAndUpCommand.ExecuteAsync(null);

        Assert.Null(fixture.ViewModel.ErrorMessage);
        Assert.False(editor.IsDirty);
        Assert.Equal(
            "services:\r\n  web:\r\n    image: nginx:1.27\r\n    restart: always\r\n",
            await ReadFileAsync(sftp, "/srv/shop/compose.yaml", token));
        Assert.Equal(["/srv/shop/compose.yaml"], Assert.Single(fixture.Host.Validations));
        var up = Assert.IsType<DockerOperation.ComposeUp>(Assert.Single(fixture.Host.Operations));
        Assert.Equal("shop", up.Project);
        Assert.Equal(["/srv/shop/compose.yaml"], up.ConfigFiles);
    }

    [Fact]
    public async Task ASaveThatComposeRejectsIsKeptButNotBroughtUp()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        await SeedFileAsync(sftp, "/srv/shop/compose.yaml", "services: {}\n", token);
        var fixture = CreateFixture(environment: EnvironmentKind.Development, composeFiles: new SftpDockerComposeFiles(sftp));
        fixture.Host.ProjectList = [new DockerComposeProject("shop", "running(1)", ["/srv/shop/compose.yaml"])];
        fixture.Host.ValidationFailure = new DockerFailure(DockerError.CommandFailed, "yaml: line 2: mapping values are not allowed in this context");
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        await fixture.ViewModel.RefreshAsync(token);
        await fixture.ViewModel.EditComposeFileCommand.ExecuteAsync(fixture.ViewModel.ComposeProjects[0]);

        fixture.ViewModel.ComposeEditor.Text = "services:\n  web: image: x\n";
        await fixture.ViewModel.SaveComposeFileAndUpCommand.ExecuteAsync(null);

        Assert.Equal("Saved, but compose finds a problem: yaml: line 2: mapping values are not allowed in this context", fixture.ViewModel.ErrorMessage);
        Assert.Equal("services:\n  web: image: x\n", await ReadFileAsync(sftp, "/srv/shop/compose.yaml", token));
        Assert.Empty(fixture.Host.Operations);
    }

    [Fact]
    public async Task AnEditMadeOnTheServerMeanwhileIsNotOverwritten()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        await SeedFileAsync(sftp, "/srv/shop/compose.yaml", "services: {}\n", token);
        var fixture = CreateFixture(composeFiles: new SftpDockerComposeFiles(sftp));
        fixture.Host.ProjectList = [new DockerComposeProject("shop", "running(1)", ["/srv/shop/compose.yaml"])];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        await fixture.ViewModel.RefreshAsync(token);
        await fixture.ViewModel.EditComposeFileCommand.ExecuteAsync(fixture.ViewModel.ComposeProjects[0]);

        await SeedFileAsync(sftp, "/srv/shop/compose.yaml", "services:\n  theirs: {}\n", token);
        fixture.ViewModel.ComposeEditor.Text = "services:\n  mine: {}\n";
        await fixture.ViewModel.SaveComposeFileCommand.ExecuteAsync(null);

        Assert.StartsWith("Not saved: '/srv/shop/compose.yaml' was changed on the server", fixture.ViewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("services:\n  theirs: {}\n", await ReadFileAsync(sftp, "/srv/shop/compose.yaml", token));
        Assert.True(fixture.ViewModel.ComposeEditor.IsDirty);
        Assert.Empty(fixture.Host.Validations);
    }

    [Fact]
    public async Task ANewFileIsWrittenCheckedAndBroughtUpUnderTheNameComposeGivesIt()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        var fixture = CreateFixture(environment: EnvironmentKind.Development, composeFiles: new SftpDockerComposeFiles(sftp));
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        fixture.ViewModel.ComposeFilePath = "/srv/blog/compose.yaml";
        var editor = fixture.ViewModel.ComposeEditor;

        await fixture.ViewModel.NewComposeFileCommand.ExecuteAsync(null);
        Assert.True(editor.IsNew);
        Assert.True(editor.IsDirty);
        Assert.Equal("/srv/blog/compose.yaml", editor.Path);
        Assert.Equal(DockerComposeEditorViewModel.NewFileTemplate, editor.Text);

        fixture.Host.ProjectList = [new DockerComposeProject("blog", "running(1)", ["/srv/blog/compose.yaml"])];
        await fixture.ViewModel.SaveComposeFileAndUpCommand.ExecuteAsync(null);

        Assert.Null(fixture.ViewModel.ErrorMessage);
        Assert.Equal(DockerComposeEditorViewModel.NewFileTemplate, await ReadFileAsync(sftp, "/srv/blog/compose.yaml", token));
        var up = Assert.IsType<DockerOperation.ComposeUp>(Assert.Single(fixture.Host.Operations));
        Assert.Null(up.Project);
        Assert.Equal(["/srv/blog/compose.yaml"], up.ConfigFiles);
        Assert.False(editor.IsNew);
        Assert.Equal("blog", editor.Project?.Name);
    }

    [Fact]
    public async Task ANewFileNeedsAFullPath()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(composeFiles: new SftpDockerComposeFiles(new FakeSftpService()));
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        await fixture.ViewModel.NewComposeFileCommand.ExecuteAsync(null);
        fixture.ViewModel.ComposeEditor.Path = "blog/compose.yaml";
        await fixture.ViewModel.SaveComposeFileCommand.ExecuteAsync(null);

        Assert.Contains("is not a full path", fixture.ViewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.True(fixture.ViewModel.ComposeEditor.IsNew);
    }

    [Fact]
    public async Task ClosingWithUnsavedChangesAsksFirst()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(confirmations: [false, true], composeFiles: new SftpDockerComposeFiles(new FakeSftpService()));
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        await fixture.ViewModel.NewComposeFileCommand.ExecuteAsync(null);

        await fixture.ViewModel.CloseComposeEditorCommand.ExecuteAsync(null);
        Assert.True(fixture.ViewModel.ComposeEditor.IsOpen);

        await fixture.ViewModel.CloseComposeEditorCommand.ExecuteAsync(null);
        Assert.False(fixture.ViewModel.ComposeEditor.IsOpen);
        Assert.Equal(2, fixture.Confirmation.Messages.Count);
    }

    [Fact]
    public async Task WithoutFileAccessTheEditorStaysShut()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.ProjectList = [new DockerComposeProject("shop", "running(1)", ["/srv/shop/compose.yaml"])];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        await fixture.ViewModel.RefreshAsync(token);

        await fixture.ViewModel.EditComposeFileCommand.ExecuteAsync(fixture.ViewModel.ComposeProjects[0]);
        await fixture.ViewModel.NewComposeFileCommand.ExecuteAsync(null);

        Assert.False(fixture.ViewModel.CanEditComposeFiles);
        Assert.False(fixture.ViewModel.ComposeEditor.IsOpen);
    }

    [AvaloniaFact]
    public async Task TheEditorTakesThePlaceOfTheProjectListAndGivesItBack()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        await SeedFileAsync(sftp, "/srv/shop/compose.yaml", "services: {}\n", token);
        var fixture = CreateFixture(composeFiles: new SftpDockerComposeFiles(sftp));
        fixture.Host.ProjectList = [new DockerComposeProject("shop", "running(1)", ["/srv/shop/compose.yaml"])];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        fixture.ViewModel.SelectedTab = DockerTab.Compose;
        await fixture.ViewModel.RefreshAsync(token);
        var window = new Window
        {
            Width = 1300,
            Height = 700,
            Content = new DockerWorkspace { DataContext = fixture.ViewModel },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await fixture.ViewModel.EditComposeFileCommand.ExecuteAsync(fixture.ViewModel.ComposeProjects[0]);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var text = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ComposeEditorText");
        Assert.True(text.IsEffectivelyVisible);
        Assert.Equal("services: {}\n", text.Text);
        Assert.False(window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "ProjectList").IsEffectivelyVisible);
        Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), block => block.Text == "/srv/shop/compose.yaml" && block.IsEffectivelyVisible);

        await fixture.ViewModel.CloseComposeEditorCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "ProjectList").IsEffectivelyVisible);
        window.Close();
        fixture.ViewModel.Deactivate();
    }

    private static async Task SeedFileAsync(FakeSftpService sftp, string path, string text, CancellationToken token)
    {
        var opened = await sftp.OpenWriteAsync(path, token);
        await using (opened.Value)
        {
            await opened.Value.WriteAsync(Encoding.UTF8.GetBytes(text), token);
        }
    }

    private static async Task<string> ReadFileAsync(FakeSftpService sftp, string path, CancellationToken token)
    {
        var opened = await sftp.OpenReadAsync(path, token);
        await using (opened.Value)
        {
            using var reader = new StreamReader(opened.Value, Encoding.UTF8);
            return await reader.ReadToEndAsync(token);
        }
    }
}
