using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Services.Docker;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>Images, volumes and compose: the parsers against output captured from a real Docker 29 engine, the
/// command lines, and the validation that stands between a name and the shell.</summary>
public sealed class DockerResourceCliTests
{
    private const string _imagesOutput = """
        {"Containers":"2","CreatedAt":"2026-10-03 14:00:18 +0300 EEST","CreatedSince":"58 minutes ago","Digest":"\u003cnone\u003e","ID":"6ac1b0bf2c77","Repository":"nginx","SharedSize":"N/A","Size":"141MB","Tag":"1.27","UniqueSize":"N/A"}
        {"Containers":"N/A","CreatedAt":"2026-10-01 08:50:33 +0300 EEST","CreatedSince":"2 days ago","Digest":"\u003cnone\u003e","ID":"fc098f8e867f","Repository":"\u003cnone\u003e","SharedSize":"N/A","Size":"495MB","Tag":"\u003cnone\u003e","UniqueSize":"N/A"}
        """;

    [Fact]
    public void ImagesAreParsedWithUntaggedOnesReferencedById()
    {
        var images = DockerCli.ParseImages(_imagesOutput);

        Assert.Equal(2, images.Count);
        Assert.Equal("nginx:1.27", images[0].Reference);
        Assert.Equal(2, images[0].Containers);
        Assert.Null(images[1].Repository);
        Assert.Null(images[1].Tag);
        Assert.Null(images[1].Containers);
        Assert.Equal("fc098f8e867f", images[1].Reference);
    }

    [Fact]
    public void VolumesAreParsedWithTheirComposeProject()
    {
        var volumes = DockerCli.ParseVolumes("""
            {"name":"rfprobe_data","driver":"local","project":"rfprobe"}
            {"name":"0f3c9d2b7a1e4f5a8b6c7d8e9f0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a","driver":"local","project":""}
            """);

        Assert.Equal("rfprobe", volumes[0].ComposeProject);
        Assert.Null(volumes[1].ComposeProject);
        Assert.Equal("local", volumes[1].Driver);
    }

    [Fact]
    public void ComposeProjectsAreOneArrayAndTheirFilesAreSplit()
    {
        var projects = DockerCli.ParseComposeProjects("""
            [{"Name":"shop","Status":"running(3)","ConfigFiles":"/srv/shop/compose.yaml,/srv/shop/compose.prod.yaml"},{"Name":"tools","Status":"exited(1)","ConfigFiles":""}]
            """);

        Assert.Equal(2, projects.Count);
        Assert.Equal(["/srv/shop/compose.yaml", "/srv/shop/compose.prod.yaml"], projects[0].ConfigFiles);
        Assert.Empty(projects[1].ConfigFiles);
        Assert.Empty(DockerCli.ParseComposeProjects("[]"));
        Assert.Empty(DockerCli.ParseComposeProjects("  "));
    }

    [Fact]
    public void ContainerRowsCarryTheVolumesTheyMount()
    {
        var containers = DockerCli.ParseContainers("""
            {"id":"a","name":"db","image":"postgres:17","state":"running","status":"Up","ports":"","created":"","project":"shop","service":"db","mounts":"shop_pgdata,0f3c9d2b"}
            """);

        Assert.Equal(["shop_pgdata", "0f3c9d2b"], containers[0].VolumeNames);
        Assert.Contains("--no-trunc", DockerCli.ListContainersCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void OperationCommandsQuoteEveryArgument()
    {
        Assert.Equal(
            "docker compose -p 'shop' -f '/srv/shop/compose.yaml' -f '/srv/my shop/override.yaml' up --detach",
            DockerCli.OperationCommand(new DockerOperation.ComposeUp(
                "shop",
                ["/srv/shop/compose.yaml", "/srv/my shop/override.yaml"])));
        Assert.Equal(
            "docker compose -f '/srv/it'\\''s/compose.yaml' up --detach",
            DockerCli.OperationCommand(new DockerOperation.ComposeUp(null, ["/srv/it's/compose.yaml"])));
        Assert.Equal(
            "docker compose -p 'shop' down",
            DockerCli.OperationCommand(new DockerOperation.ComposeDown("shop", RemoveVolumes: false)));
        Assert.Equal(
            "docker compose -p 'shop' down --volumes",
            DockerCli.OperationCommand(new DockerOperation.ComposeDown("shop", RemoveVolumes: true)));
        Assert.Equal(
            "docker pull 'ghcr.io/acme/api:2.4.1'",
            DockerCli.OperationCommand(new DockerOperation.PullImage("ghcr.io/acme/api:2.4.1")));
        Assert.Equal("docker image prune --force", DockerCli.OperationCommand(new DockerOperation.PruneImages()));
        Assert.Equal("docker volume prune --force", DockerCli.OperationCommand(new DockerOperation.PruneVolumes()));
        Assert.Equal("docker image rm 'nginx:1.27'", DockerCli.RemoveImageCommand("nginx:1.27"));
        Assert.Equal("docker volume rm 'shop_pgdata'", DockerCli.RemoveVolumeCommand("shop_pgdata"));
    }

    [Theory]
    [InlineData("nginx")]
    [InlineData("nginx:1.27")]
    [InlineData("registry.example.com:5000/team/app:v2")]
    [InlineData("postgres@sha256:0f3c9d2b7a1e4f5a8b6c7d8e9f0a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a")]
    [InlineData("fc098f8e867f")]
    public void ImageReferencesDockerAcceptsAreAccepted(string reference)
    {
        Assert.True(DockerCli.IsValidImageReference(reference));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-a")]
    [InlineData("nginx; reboot")]
    [InlineData("nginx $(id)")]
    [InlineData("nginx\n")]
    [InlineData("ngi'nx")]
    public void ImageReferencesThatCouldReachTheShellAreRejected(string reference)
    {
        Assert.False(DockerCli.IsValidImageReference(reference));
        Assert.NotNull(DockerCli.FindInvalidArgument(new DockerOperation.PullImage(reference)));
    }

    [Fact]
    public void ComposeArgumentsAreValidatedBeforeAnyCommandIsBuilt()
    {
        Assert.Null(DockerCli.FindInvalidArgument(new DockerOperation.ComposeUp("shop", ["/srv/shop/compose.yaml"])));
        Assert.NotNull(DockerCli.FindInvalidArgument(new DockerOperation.ComposeUp("Shop!", ["/srv/a.yaml"])));
        Assert.NotNull(DockerCli.FindInvalidArgument(new DockerOperation.ComposeUp("shop", [])));
        Assert.NotNull(DockerCli.FindInvalidArgument(new DockerOperation.ComposeUp("shop", ["/srv/a.yaml\nreboot"])));
        Assert.NotNull(DockerCli.FindInvalidArgument(new DockerOperation.ComposeDown("-shop", false)));
        Assert.False(DockerCli.IsValidVolumeName("data; reboot"));
    }

    [Theory]
    [InlineData("docker: 'compose' is not a docker command.\nSee 'docker --help'")]
    [InlineData("unknown command \"compose\" for \"docker\"")]
    public void AMissingComposePluginIsNamed(string standardError)
    {
        Assert.Equal(DockerError.ComposeNotInstalled, DockerCli.DescribeFailure(1, standardError).Error);
    }

    [Fact]
    public async Task HostRefusesBadArgumentsWithoutSendingAnything()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new FakeSshConnection();
        var host = new DockerCliHost(connection);

        var pull = await host.StartOperationAsync(new DockerOperation.PullImage("x; reboot"), token);
        var image = await host.RemoveImageAsync("x y", token);
        var volume = await host.RemoveVolumeAsync("$(reboot)", token);

        Assert.Equal(DockerError.InvalidArgument, pull.Failure.Error);
        Assert.Equal(DockerError.InvalidArgument, image.Failure.Error);
        Assert.Equal(DockerError.InvalidArgument, volume.Failure.Error);
        Assert.EndsWith("so nothing was sent to the server.", pull.Failure.Message, StringComparison.Ordinal);
        Assert.Empty(connection.StartedCommands);
    }

    [Fact]
    public async Task HostStreamsOperations()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new FakeSshConnection
        {
            StartCommand = _ => FakeSshRunningCommand.Completed(0, " Container shop-api-1 Started"),
        };
        var host = new DockerCliHost(connection);

        var started = await host.StartOperationAsync(new DockerOperation.ComposeDown("shop", false), token);

        Assert.True(started.IsSuccess);
        Assert.Equal("docker compose -p 'shop' down", Assert.Single(connection.StartedCommands));
    }

    [Fact]
    public async Task MemoryKeepsProjectsThatDropOffTheListAndForgetsOnRequest()
    {
        var token = TestContext.Current.CancellationToken;
        using var memory = new SettingsDockerComposeProjectMemory(new InMemorySettingsStore());
        var server = Guid.NewGuid();
        var other = Guid.NewGuid();

        await memory.RememberAsync(server, [
            new DockerComposeProject("shop", "running(2)", ["/srv/shop/compose.yaml"]),
            new DockerComposeProject("adhoc", "running(1)", []),
        ], token);
        await memory.RememberAsync(other, [new DockerComposeProject("shop", "running(1)", ["/opt/shop.yaml"])], token);
        await memory.RememberAsync(server, [], token);
        await memory.RememberAsync(server, [new DockerComposeProject("shop", "running(2)", ["/srv/shop/v2.yaml"])], token);

        var remembered = Assert.Single(await memory.RecallAsync(server, token));
        Assert.Equal("shop", remembered.Name);
        Assert.Equal(["/srv/shop/v2.yaml"], remembered.ConfigFiles);
        Assert.Equal(["/opt/shop.yaml"], Assert.Single(await memory.RecallAsync(other, token)).ConfigFiles);

        await memory.ForgetAsync(server, "shop", token);
        Assert.Empty(await memory.RecallAsync(server, token));
        _ = Assert.Single(await memory.RecallAsync(other, token));
    }
}
