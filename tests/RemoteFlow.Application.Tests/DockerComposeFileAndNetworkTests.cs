using System.Text;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Services.Docker;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

/// <summary>Container addresses and ports, compose validation, and compose files read and written over SFTP.</summary>
public sealed class DockerComposeFileAndNetworkTests
{
    private const string _psOutput = """
        {"id":"aaa111","name":"web","image":"nginx","state":"running","status":"Up","ports":"0.0.0.0:8080->80/tcp, [::]:8080->80/tcp","created":"","project":"","service":""}
        {"id":"bbb222","name":"job","image":"busybox","state":"exited","status":"Exited (0)","ports":"","created":"","project":"","service":""}
        {"id":"ccc333","name":"agent","image":"agent","state":"running","status":"Up","ports":"","created":"","project":"","service":""}
        """;

    // As Docker 29 writes it, trimmed to the fields that are read.
    private const string _inspectOutput = """
        {"id":"aaa111","networks":{"shop_default":{"Gateway":"172.18.0.1","IPAddress":"172.18.0.2","GlobalIPv6Address":""},"bridge":{"IPAddress":"172.17.0.3","GlobalIPv6Address":"fd00::3"}}}
        {"id":"ccc333","networks":{"host":{"Gateway":"","IPAddress":"","GlobalIPv6Address":""}}}
        """;

    [Fact]
    public void NetworksAreReadPerContainerWithEmptyAddressesAsNone()
    {
        var networks = DockerCli.ParseNetworks(_inspectOutput);

        Assert.Equal(
            [new DockerContainerNetwork("shop_default", "172.18.0.2", null), new DockerContainerNetwork("bridge", "172.17.0.3", "fd00::3")],
            networks["aaa111"]);
        Assert.Equal([new DockerContainerNetwork("host", null, null)], networks["ccc333"]);
    }

    [Fact]
    public async Task OnlyRunningContainersAreInspectedAndInOneCommand()
    {
        var token = TestContext.Current.CancellationToken;
        var sent = new List<string>();
        var connection = new FakeSshConnection
        {
            Execute = command =>
            {
                sent.Add(command);
                return command.StartsWith("docker inspect", StringComparison.Ordinal)
                    ? new(0, _inspectOutput, string.Empty)
                    : new(0, _psOutput, string.Empty);
            },
        };
        var host = new DockerCliHost(connection);

        var listed = await host.ListContainersAsync(token);

        Assert.Equal(2, sent.Count);
        Assert.Equal(
            "docker inspect --type container --format '{\"id\":{{json .Id}},\"networks\":{{json .NetworkSettings.Networks}}}' 'aaa111' 'ccc333'",
            sent[1]);
        Assert.Equal("172.18.0.2", listed.Value[0].NetworkList[0].IPv4Address);
        Assert.Empty(listed.Value[1].NetworkList);
        Assert.Equal("host", Assert.Single(listed.Value[2].NetworkList).Name);
    }

    [Fact]
    public async Task AFailedInspectStillListsTheContainers()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new FakeSshConnection
        {
            Execute = command => command.StartsWith("docker inspect", StringComparison.Ordinal)
                ? new(1, "{\"id\":\"aaa111\",\"networks\":{\"bridge\":{\"IPAddress\":\"172.17.0.2\"}}}", "Error: No such container: ccc333")
                : new(0, _psOutput, string.Empty),
        };
        var host = new DockerCliHost(connection);

        var listed = await host.ListContainersAsync(token);

        Assert.True(listed.IsSuccess);
        Assert.Equal("172.17.0.2", Assert.Single(listed.Value[0].NetworkList).IPv4Address);
        Assert.Empty(listed.Value[2].NetworkList);
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("0.0.0.0:8080->80/tcp, [::]:8080->80/tcp", new[] { "8080→80/tcp" })]
    [InlineData("0.0.0.0:8080->80/tcp, :::8080->80/tcp, 443/tcp", new[] { "8080→80/tcp", "443/tcp" })]
    [InlineData("127.0.0.1:5432->5432/tcp", new[] { "127.0.0.1:5432→5432/tcp" })]
    [InlineData("0.0.0.0:8000-8001->8000-8001/udp", new[] { "8000-8001→8000-8001/udp" })]
    public void PortsAreShortenedToWhatIsPublishedWhere(string ports, string[] expected)
    {
        Assert.Equal(expected, DockerCli.SummarizePorts(ports));
    }

    [Fact]
    public async Task ComposeIsCheckedWithEveryFileQuotedAndBadPathsNeverSent()
    {
        var token = TestContext.Current.CancellationToken;
        var sent = new List<string>();
        var connection = new FakeSshConnection
        {
            Execute = command =>
            {
                sent.Add(command);
                return new(15, string.Empty, "validating /srv/shop/compose.yaml: services.web additional properties 'imgae' not allowed");
            },
        };
        var host = new DockerCliHost(connection);

        var checkedFiles = await host.ValidateComposeAsync(["/srv/shop/compose.yaml", "/srv/my shop/o'ride.yaml"], token);
        var refused = await host.ValidateComposeAsync(["/srv/a.yaml\nreboot"], token);

        Assert.Equal(
            "docker compose -f '/srv/shop/compose.yaml' -f '/srv/my shop/o'\\''ride.yaml' config --quiet",
            Assert.Single(sent));
        Assert.Equal(DockerError.CommandFailed, checkedFiles.Failure.Error);
        Assert.Contains("additional properties 'imgae'", checkedFiles.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(DockerError.InvalidArgument, refused.Failure.Error);
    }

    [Fact]
    public async Task AComposeFileIsReadAndSavedInPlaceKeepingItsPermissions()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        await SeedAsync(sftp, "/srv/shop/compose.yaml", "services:\n  web:\n    image: nginx:1.26\n", token);
        _ = await sftp.SetPermissionsAsync("/srv/shop/compose.yaml", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, token);
        var files = new SftpDockerComposeFiles(sftp);

        var read = await files.ReadAsync("/srv/shop/compose.yaml", token);
        var written = await files.WriteAsync("/srv/shop/compose.yaml", "services:\n  web:\n    image: nginx:1.27\n", read.Value, token);

        Assert.True(written.IsSuccess);
        Assert.Equal("services:\n  web:\n    image: nginx:1.27\n", (await files.ReadAsync("/srv/shop/compose.yaml", token)).Value);
        var stat = await sftp.StatAsync("/srv/shop/compose.yaml", token);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, stat.Value!.Mode);
        _ = Assert.Single((await sftp.ListAsync("/srv/shop", token)).Value);
    }

    [Fact]
    public async Task ASaveRefusesWhenTheFileChangedOnTheServer()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        await SeedAsync(sftp, "/srv/shop/compose.yaml", "someone else's edit\n", token);
        var files = new SftpDockerComposeFiles(sftp);

        var written = await files.WriteAsync("/srv/shop/compose.yaml", "mine\n", "what I opened\n", token);

        Assert.Equal(DockerError.FileChanged, written.Failure.Error);
        Assert.Equal("someone else's edit\n", (await files.ReadAsync("/srv/shop/compose.yaml", token)).Value);
    }

    [Fact]
    public async Task ANewFileGetsItsFoldersAndNeverReplacesAnExistingOne()
    {
        var token = TestContext.Current.CancellationToken;
        var sftp = new FakeSftpService();
        var files = new SftpDockerComposeFiles(sftp);

        var created = await files.WriteAsync("/srv/blog/compose.yaml", "services: {}\n", expected: null, token);
        var again = await files.WriteAsync("/srv/blog/compose.yaml", "other\n", expected: null, token);

        Assert.True(created.IsSuccess);
        Assert.True((await sftp.StatAsync("/srv/blog", token)).Value!.IsDirectory);
        Assert.Equal(DockerError.FileChanged, again.Failure.Error);
        Assert.Equal("services: {}\n", (await files.ReadAsync("/srv/blog/compose.yaml", token)).Value);
    }

    [Theory]
    [InlineData("compose.yaml")]
    [InlineData("~/app/compose.yaml")]
    [InlineData("/srv/app/")]
    [InlineData("/srv/a.yaml\nreboot")]
    public async Task OnlyAFullPathToAFileIsAccepted(string path)
    {
        var token = TestContext.Current.CancellationToken;
        var files = new SftpDockerComposeFiles(new FakeSftpService());

        Assert.Equal(DockerError.InvalidArgument, (await files.ReadAsync(path, token)).Failure.Error);
        Assert.Equal(DockerError.InvalidArgument, (await files.WriteAsync(path, "x", null, token)).Failure.Error);
    }

    [Fact]
    public async Task AMissingFileSaysSo()
    {
        var token = TestContext.Current.CancellationToken;
        var files = new SftpDockerComposeFiles(new FakeSftpService());

        var read = await files.ReadAsync("/srv/gone/compose.yaml", token);

        Assert.Equal(DockerError.FileUnavailable, read.Failure.Error);
        Assert.Contains("does not exist", read.Failure.Message, StringComparison.Ordinal);
    }

    private static async Task SeedAsync(FakeSftpService sftp, string path, string text, CancellationToken token)
    {
        var opened = await sftp.OpenWriteAsync(path, token);
        await using (opened.Value)
        {
            await opened.Value.WriteAsync(Encoding.UTF8.GetBytes(text), token);
        }
    }
}
