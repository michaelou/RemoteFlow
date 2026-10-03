using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using RemoteFlow.Domain.Enums;
using RemoteFlow.TestSupport;
using RemoteFlow.UI.Services;
using RemoteFlow.UI.ViewModels.Docker;
using RemoteFlow.UI.Views.Docker;
using Xunit;

namespace RemoteFlow.UI.Tests;

public sealed partial class DockerWorkspaceTests
{
    [Fact]
    public async Task AttachingListsContainersByProjectThenNameWithTheirStats()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers =
        [
            Container("c3", "standalone", DockerContainerState.Running),
            Container("c2", "worker", DockerContainerState.Exited, project: "shop"),
            Container("c1", "api", DockerContainerState.Running, project: "shop"),
        ];
        fixture.Host.Stats = [new DockerContainerStats("c1", "api", 12.5, 3, "40MiB / 2GiB", "-", "-")];

        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        Assert.True(fixture.ViewModel.IsConnected);
        Assert.Null(fixture.ViewModel.ErrorMessage);
        Assert.Equal("Docker 27.3.1", fixture.ViewModel.ServerVersion);
        Assert.Equal(["api", "worker", "standalone"], fixture.ViewModel.Containers.Select(item => item.Name));
        Assert.Equal("3 containers, 2 running", fixture.ViewModel.Summary);
        Assert.Equal("40MiB / 2GiB", fixture.ViewModel.Containers[0].MemoryText);
        Assert.Equal("—", fixture.ViewModel.Containers[1].CpuText);
    }

    [Fact]
    public async Task AServerWithoutDockerAccessSaysWhyAndStaysDisconnected()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.ProbeFailure = new DockerFailure(DockerError.PermissionDenied, "The account 'deploy' is not allowed to use Docker.");

        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        Assert.False(fixture.ViewModel.IsConnected);
        Assert.Contains("not allowed to use Docker", fixture.ViewModel.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Host.ListCalls);
        Assert.True(fixture.Ssh.IsDisconnected);
    }

    [Fact]
    public async Task RefreshKeepsRowsAndSelectionAndDropsContainersThatAreGone()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers =
        [
            Container("c1", "api", DockerContainerState.Running),
            Container("c2", "db", DockerContainerState.Running),
        ];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var api = fixture.ViewModel.Containers[0];
        fixture.ViewModel.SelectedContainer = api;

        fixture.Host.Containers = [Container("c1", "api", DockerContainerState.Exited)];
        await fixture.ViewModel.RefreshAsync(token);

        Assert.Same(api, Assert.Single(fixture.ViewModel.Containers));
        Assert.Same(api, fixture.ViewModel.SelectedContainer);
        Assert.False(api.IsRunning);
        Assert.True(api.CanStart);
        Assert.True(api.CanRemove);
    }

    [Fact]
    public async Task FilterAndStoppedToggleNarrowTheList()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers =
        [
            Container("c1", "api", DockerContainerState.Running, image: "shop/api:1"),
            Container("c2", "db", DockerContainerState.Exited, image: "postgres:17"),
        ];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        fixture.ViewModel.FilterText = "postgres";
        Assert.Equal(["db"], fixture.ViewModel.Containers.Select(item => item.Name));

        fixture.ViewModel.ShowStopped = false;
        Assert.Empty(fixture.ViewModel.Containers);
        Assert.True(fixture.ViewModel.HasNoContainers);
        Assert.Equal("No container matches the filter.", fixture.ViewModel.NoContainersMessage);

        fixture.ViewModel.FilterText = string.Empty;
        Assert.Equal(["api"], fixture.ViewModel.Containers.Select(item => item.Name));
    }

    [Fact]
    public async Task RemovingAsksFirstAndActsOnTheIdNotTheName()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(confirmations: [false, true]);
        fixture.Host.Containers = [Container("c2", "db", DockerContainerState.Exited)];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var db = fixture.ViewModel.Containers[0];

        await fixture.ViewModel.RemoveContainerCommand.ExecuteAsync(db);
        Assert.Empty(fixture.Host.Actions);

        fixture.Host.Containers = [];
        await fixture.ViewModel.RemoveContainerCommand.ExecuteAsync(db);

        Assert.Equal(("c2", DockerAction.Remove), Assert.Single(fixture.Host.Actions));
        Assert.Equal(2, fixture.Confirmation.Messages.Count);
        Assert.Empty(fixture.ViewModel.Containers);
        Assert.Equal("Removed db.", fixture.ViewModel.FeedbackMessage);
    }

    [Theory]
    [InlineData(EnvironmentKind.Production, 1)]
    [InlineData(EnvironmentKind.Development, 0)]
    public async Task StoppingAsksOnlyOnProduction(EnvironmentKind environment, int expectedPrompts)
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(environment);
        fixture.Host.Containers = [Container("c1", "api", DockerContainerState.Running)];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        await fixture.ViewModel.StopContainerCommand.ExecuteAsync(fixture.ViewModel.Containers[0]);

        Assert.Equal(expectedPrompts, fixture.Confirmation.Messages.Count);
        Assert.Equal(("c1", DockerAction.Stop), Assert.Single(fixture.Host.Actions));
    }

    [Fact]
    public async Task AFailedActionIsReportedInlineAndTheRowIsUsableAgain()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers = [Container("c1", "api", DockerContainerState.Exited)];
        fixture.Host.ActionFailure = new DockerFailure(DockerError.CommandFailed, "Error response from daemon: port is already allocated");
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var api = fixture.ViewModel.Containers[0];

        await fixture.ViewModel.StartContainerCommand.ExecuteAsync(api);

        Assert.Equal("'api' could not be started: Error response from daemon: port is already allocated", fixture.ViewModel.ErrorMessage);
        Assert.False(api.IsBusy);
        Assert.True(api.CanStart);
    }

    [Fact]
    public async Task OpeningAShellHandsTheIdAndNameToTheTerminalOpener()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers = [Container("c1", "api", DockerContainerState.Running)];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);

        await fixture.ViewModel.OpenShellCommand.ExecuteAsync(fixture.ViewModel.Containers[0]);

        Assert.Equal((fixture.Connection.Id, "web-01", "c1", "api"), Assert.Single(fixture.Shells.Opened));
    }

    [Fact]
    public async Task LogsArriveInOrderMarkErrorsAndStopWhenThePaneCloses()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers = [Container("c1", "api", DockerContainerState.Running)];
        var stream = new FakeSshRunningCommand();
        fixture.Host.Logs = stream;
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var logs = fixture.ViewModel.Logs;
        logs.BatchInterval = TimeSpan.Zero;

        await fixture.ViewModel.ShowLogsCommand.ExecuteAsync(fixture.ViewModel.Containers[0]);
        stream.Publish("listening on :8080");
        stream.Publish("warning: slow query", SshOutputKind.StandardError);
        await WaitUntilAsync(() => logs.Lines.Count == 2, token);

        Assert.True(logs.IsOpen);
        Assert.True(logs.IsStreaming);
        Assert.Equal("Logs — api", logs.Heading);
        Assert.Equal(new DockerLogLine("listening on :8080", false), logs.Lines[0]);
        Assert.True(logs.Lines[1].IsError);
        Assert.Equal(("c1", new DockerLogOptions { Tail = 500, Follow = true }), Assert.Single(fixture.Host.LogRequests));

        logs.FilterText = "slow";
        Assert.Equal(["warning: slow query"], logs.Lines.Select(line => line.Text));

        await logs.CloseCommand.ExecuteAsync(null);

        Assert.False(logs.IsOpen);
        Assert.False(logs.IsStreaming);
        Assert.True(stream.IsDisposed);
        Assert.Empty(logs.Lines);
    }

    [Fact]
    public async Task ASnapshotThatEndsSaysNothingButAFailedOneSaysWhy()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers = [Container("c1", "api", DockerContainerState.Exited)];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var logs = fixture.ViewModel.Logs;
        logs.BatchInterval = TimeSpan.Zero;
        logs.Follow = false;

        fixture.Host.Logs = FakeSshRunningCommand.Completed(0, "one", "two");
        await fixture.ViewModel.ShowLogsCommand.ExecuteAsync(fixture.ViewModel.Containers[0]);
        await logs.Completion;
        Assert.Equal(2, logs.Lines.Count);
        Assert.Null(logs.StatusMessage);

        fixture.Host.Logs = FakeSshRunningCommand.Completed(1);
        await logs.ReloadCommand.ExecuteAsync(null);
        await logs.Completion;
        Assert.Empty(logs.Lines);
        Assert.Equal("docker logs exited with code 1.", logs.StatusMessage);
    }

    [Fact]
    public async Task ThePaneKeepsOnlyTheNewestLines()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers = [Container("c1", "api", DockerContainerState.Running)];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var logs = fixture.ViewModel.Logs;
        logs.BatchInterval = TimeSpan.Zero;
        logs.Follow = false;
        fixture.Host.Logs = FakeSshRunningCommand.Completed(
            0,
            [.. Enumerable.Range(0, DockerLogsViewModel.MaxLines + 10).Select(index => $"line {index}")]);

        await fixture.ViewModel.ShowLogsCommand.ExecuteAsync(fixture.ViewModel.Containers[0]);
        await logs.Completion;

        Assert.Equal(DockerLogsViewModel.MaxLines, logs.Lines.Count);
        Assert.Equal("line 10", logs.Lines[0].Text);
    }

    [AvaloniaFact]
    public async Task TheViewRendersTheListWithColumnsThatLineUp()
    {
        var token = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        fixture.Host.Containers =
        [
            Container("c1", "api", DockerContainerState.Running, project: "shop", ports: "0.0.0.0:8080->80/tcp",
                networks: [new DockerContainerNetwork("shop_default", "172.18.0.4", null)]),
        ];
        await fixture.ViewModel.AttachAsync(fixture.Connection.Id, token);
        var window = new Window
        {
            Width = 1300,
            Height = 700,
            Content = new DockerWorkspace { DataContext = fixture.ViewModel },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // The headings sit in a different grid from the rows; only matching geometry keeps them aligned. A
        // fixed column and a star column are both checked, because the star ones also need equal widths.
        AssertSameLeft(window, "Name", "api");
        AssertSameLeft(window, "Project", "shop");
        AssertSameLeft(window, "IP", "172.18.0.4");
        AssertSameLeft(window, "Ports", "8080→80/tcp");
        window.Close();
        fixture.ViewModel.Deactivate();
    }

    private static void AssertSameLeft(Window window, string heading, string cell)
    {
        var texts = window.GetVisualDescendants().OfType<TextBlock>().ToList();
        var headingLeft = texts.First(text => text.Text == heading).TranslatePoint(default, window);
        var cellLeft = texts.First(text => text.Text == cell).TranslatePoint(default, window);
        Assert.True(headingLeft.HasValue && cellLeft.HasValue);
        Assert.Equal(headingLeft.Value.X, cellLeft.Value.X, 1.0);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static DockerContainer Container(
        string id,
        string name,
        DockerContainerState state,
        string? project = null,
        string image = "image:latest",
        IReadOnlyList<string>? volumes = null,
        string ports = "",
        IReadOnlyList<DockerContainerNetwork>? networks = null)
    {
        return new DockerContainer(id, name, image, state, state.ToString(), ports, string.Empty, project, null, volumes, networks);
    }

    private static Fixture CreateFixture(
        EnvironmentKind environment = EnvironmentKind.Production,
        bool[]? confirmations = null,
        IDockerComposeProjectMemory? composeMemory = null,
        IDockerComposeFiles? composeFiles = null)
    {
        var guids = SystemGuidProvider.Instance;
        var connection = Connection.Create(guids, "web-01", "web.example", ProtocolType.Ssh).Value;
        _ = connection.SetDetails("deploy", AuthMethod.Password, null, environment, null, guids);
        var ssh = new FakeSshConnection();
        var host = new ScriptedDockerHost();
        var confirmation = new QueuedConfirmation(confirmations ?? [true, true, true]);
        var shells = new RecordingShellOpener();
        var viewModel = new DockerWorkspaceViewModel(
            new StubSessionFactory(new DockerWorkspaceSession(connection, ssh, host, composeFiles)),
            confirmation,
            shells,
            composeMemory: composeMemory);
        viewModel.Logs.BatchInterval = TimeSpan.Zero;
        return new Fixture(connection, ssh, host, confirmation, shells, viewModel);
    }

    private sealed record Fixture(
        Connection Connection,
        FakeSshConnection Ssh,
        ScriptedDockerHost Host,
        QueuedConfirmation Confirmation,
        RecordingShellOpener Shells,
        DockerWorkspaceViewModel ViewModel);

    private sealed class ScriptedDockerHost : IDockerHost
    {
        public IReadOnlyList<DockerContainer> Containers { get; set; } = [];

        public IReadOnlyList<DockerContainerStats> Stats { get; set; } = [];

        public DockerFailure? ProbeFailure { get; set; }

        public DockerFailure? ActionFailure { get; set; }

        public ISshRunningCommand Logs { get; set; } = FakeSshRunningCommand.Completed(0);

        public int ListCalls { get; private set; }

        public List<(string Container, DockerAction Action)> Actions { get; } = [];

        public List<(string Container, DockerLogOptions Options)> LogRequests { get; } = [];

        public Task<DockerResult<DockerHostInfo>> ProbeAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ProbeFailure is null
                ? DockerResult<DockerHostInfo>.Success(new DockerHostInfo("27.3.1"))
                : DockerResult<DockerHostInfo>.Fail(ProbeFailure));
        }

        public Task<DockerResult<IReadOnlyList<DockerContainer>>> ListContainersAsync(
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult(DockerResult<IReadOnlyList<DockerContainer>>.Success(Containers));
        }

        public Task<DockerResult<IReadOnlyList<DockerContainerStats>>> GetStatsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DockerResult<IReadOnlyList<DockerContainerStats>>.Success(Stats));
        }

        public Task<DockerResult> RunActionAsync(
            string container,
            DockerAction action,
            CancellationToken cancellationToken = default)
        {
            Actions.Add((container, action));
            return Task.FromResult(ActionFailure is null
                ? DockerResult.Success()
                : DockerResult.Fail(ActionFailure.Error, ActionFailure.Message));
        }

        public Task<DockerResult<ISshRunningCommand>> OpenLogsAsync(
            string container,
            DockerLogOptions options,
            CancellationToken cancellationToken = default)
        {
            LogRequests.Add((container, options));
            return Task.FromResult(DockerResult<ISshRunningCommand>.Success(Logs));
        }

        public IReadOnlyList<DockerImage> ImageList { get; set; } = [];

        public IReadOnlyList<DockerVolume> VolumeList { get; set; } = [];

        public IReadOnlyList<DockerComposeProject> ProjectList { get; set; } = [];

        public DockerFailure? ComposeFailure { get; set; }

        public List<DockerOperation> Operations { get; } = [];

        public List<string> RemovedImages { get; } = [];

        public List<string> RemovedVolumes { get; } = [];

        /// <summary>What the next operation streams. Defaults to one that finishes at once with exit code 0.</summary>
        public Func<DockerOperation, ISshRunningCommand> OperationOutput { get; set; } = _ => FakeSshRunningCommand.Completed(0);

        public Task<DockerResult<IReadOnlyList<DockerImage>>> ListImagesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DockerResult<IReadOnlyList<DockerImage>>.Success(ImageList));
        }

        public Task<DockerResult> RemoveImageAsync(string reference, CancellationToken cancellationToken = default)
        {
            RemovedImages.Add(reference);
            ImageList = [.. ImageList.Where(image => image.Reference != reference)];
            return Task.FromResult(DockerResult.Success());
        }

        public Task<DockerResult<IReadOnlyList<DockerVolume>>> ListVolumesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DockerResult<IReadOnlyList<DockerVolume>>.Success(VolumeList));
        }

        public Task<DockerResult> RemoveVolumeAsync(string name, CancellationToken cancellationToken = default)
        {
            RemovedVolumes.Add(name);
            VolumeList = [.. VolumeList.Where(volume => volume.Name != name)];
            return Task.FromResult(DockerResult.Success());
        }

        public Task<DockerResult<IReadOnlyList<DockerComposeProject>>> ListComposeProjectsAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ComposeFailure is null
                ? DockerResult<IReadOnlyList<DockerComposeProject>>.Success(ProjectList)
                : DockerResult<IReadOnlyList<DockerComposeProject>>.Fail(ComposeFailure));
        }

        public Task<DockerResult<ISshRunningCommand>> StartOperationAsync(
            DockerOperation operation,
            CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            return Task.FromResult(DockerResult<ISshRunningCommand>.Success(OperationOutput(operation)));
        }

        /// <summary>What the next compose check reports; null means compose accepts the files.</summary>
        public DockerFailure? ValidationFailure { get; set; }

        public List<IReadOnlyList<string>> Validations { get; } = [];

        public Task<DockerResult> ValidateComposeAsync(
            IReadOnlyList<string> configFiles,
            CancellationToken cancellationToken = default)
        {
            Validations.Add(configFiles);
            return Task.FromResult(ValidationFailure is null
                ? DockerResult.Success()
                : DockerResult.Fail(ValidationFailure.Error, ValidationFailure.Message));
        }
    }

    private sealed class StubSessionFactory(DockerWorkspaceSession session) : IDockerWorkspaceSessionFactory
    {
        public Task<DockerWorkspaceSession> OpenAsync(Guid connectionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal(session.Definition.Id, connectionId);
            return Task.FromResult(session);
        }
    }

    private sealed class QueuedConfirmation(bool[] answers) : IConfirmationDialogService
    {
        private int _next;

        public List<string> Messages { get; } = [];

        public Task<bool> ConfirmAsync(
            string title,
            string message,
            string confirmLabel,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.FromResult(answers[_next++]);
        }
    }

    private sealed class RecordingShellOpener : IContainerShellOpener
    {
        public List<(Guid ConnectionId, string ConnectionName, string ContainerId, string ContainerName)> Opened { get; } = [];

        public Task OpenAsync(
            Guid connectionId,
            string connectionName,
            string containerId,
            string containerName,
            CancellationToken cancellationToken = default)
        {
            Opened.Add((connectionId, connectionName, containerId, containerName));
            return Task.CompletedTask;
        }
    }
}
