using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Abstractions.Ssh;
using RemoteFlow.Application.Services.Docker;
using RemoteFlow.TestSupport;
using Xunit;

namespace RemoteFlow.Application.Tests;

public sealed class DockerCliTests
{
    private const string _psOutput = """
        {"id":"3f2a1b4c5d6e","name":"web","image":"nginx:1.27","state":"running","status":"Up 2 hours","ports":"0.0.0.0:80->80/tcp","created":"2026-10-01 10:00:00 +0000 UTC","project":"shop","service":"web"}
        {"id":"9a8b7c6d5e4f","name":"migrate","image":"shop/api:latest","state":"exited","status":"Exited (0) 3 hours ago","ports":"","created":"2026-10-01 09:00:00 +0000 UTC","project":"","service":""}
        {"id":"0011aabbccdd","name":"odd","image":"x","state":"hibernating","status":"?","ports":"","created":"","project":"","service":""}

        """;

    private const string _statsOutput = """
        {"BlockIO":"1.2MB / 0B","CPUPerc":"0.15%","Container":"3f2a1b4c5d6e","ID":"3f2a1b4c5d6e","MemPerc":"1.23%","MemUsage":"24.5MiB / 1.944GiB","Name":"web","NetIO":"1.1kB / 0B","PIDs":"3"}
        {"BlockIO":"--","CPUPerc":"--","Container":"aaaaaaaaaaaa","ID":"aaaaaaaaaaaa","MemPerc":"--","MemUsage":"-- / --","Name":"booting","NetIO":"--","PIDs":"0"}
        """;

    [Theory]
    [InlineData("web")]
    [InlineData("shop-web-1")]
    [InlineData("shop_web.1")]
    [InlineData("3f2a1b4c5d6e")]
    public void DockerNamesAndIdsAreAccepted(string reference)
    {
        Assert.True(DockerCli.IsValidContainerReference(reference));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-rm")]
    [InlineData("web; rm -rf /")]
    [InlineData("web$(id)")]
    [InlineData("we'b")]
    [InlineData("web other")]
    [InlineData("a,b")]
    [InlineData("web\n")]
    public void AnythingThatCouldReachTheShellIsRejected(string reference)
    {
        Assert.False(DockerCli.IsValidContainerReference(reference));
        _ = Assert.Throws<ArgumentException>(() => DockerCli.ActionCommand(reference, DockerAction.Stop));
        _ = Assert.Throws<ArgumentException>(() => DockerCli.ShellCommand(reference));
    }

    [Fact]
    public void QuotingSurvivesASingleQuote()
    {
        Assert.Equal("'it'\\''s'", DockerCli.Quote("it's"));
    }

    [Fact]
    public void CommandsQuoteTheContainerAndSpellOutTheirFlags()
    {
        Assert.Equal("docker stop 'web'", DockerCli.ActionCommand("web", DockerAction.Stop));
        Assert.Equal("docker rm 'web'", DockerCli.ActionCommand("web", DockerAction.Remove));
        Assert.Equal(
            "docker logs --tail 200 --follow --timestamps 'web'",
            DockerCli.LogsCommand("web", new DockerLogOptions { Tail = 200, Follow = true, Timestamps = true }));
        Assert.Equal("docker logs --tail 500 'web'", DockerCli.LogsCommand("web", new DockerLogOptions()));
        Assert.StartsWith("exec docker exec -it 'web' sh -c '", DockerCli.ShellCommand("web"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheListFormatIsOneSingleQuotedWord()
    {
        // The template carries double quotes and braces; it reaches docker intact only if the shell sees one
        // single-quoted argument with no quote of its own inside.
        var command = DockerCli.ListContainersCommand;
        var format = command["docker ps --all --format ".Length..];
        Assert.StartsWith("'{", format, StringComparison.Ordinal);
        Assert.EndsWith("}'", format, StringComparison.Ordinal);
        Assert.DoesNotContain('\'', format[1..^1]);
        Assert.Contains("com.docker.compose.project", format, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerRowsAreParsedWithComposeLabelsAndUnknownStatesTolerated()
    {
        var containers = DockerCli.ParseContainers(_psOutput);

        Assert.Equal(3, containers.Count);
        var web = containers[0];
        Assert.Equal("3f2a1b4c5d6e", web.Id);
        Assert.Equal(DockerContainerState.Running, web.State);
        Assert.True(web.IsRunning);
        Assert.Equal("shop", web.ComposeProject);
        Assert.Equal("0.0.0.0:80->80/tcp", web.Ports);
        Assert.Null(containers[1].ComposeProject);
        Assert.Equal(DockerContainerState.Exited, containers[1].State);
        Assert.Equal(DockerContainerState.Unknown, containers[2].State);
    }

    [Fact]
    public void StatsAreParsedAndPlaceholdersBecomeNull()
    {
        var stats = DockerCli.ParseStats(_statsOutput);

        Assert.Equal(2, stats.Count);
        Assert.Equal(0.15, stats[0].CpuPercent);
        Assert.Equal(1.23, stats[0].MemoryPercent);
        Assert.Equal("24.5MiB / 1.944GiB", stats[0].MemoryUsage);
        Assert.Null(stats[1].CpuPercent);
    }

    [Theory]
    [InlineData(127, "bash: docker: command not found", DockerError.NotInstalled)]
    [InlineData(1, "permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock: Get \"http://%2Fvar%2Frun%2Fdocker.sock/v1.47/containers/json\": dial unix /var/run/docker.sock: connect: permission denied", DockerError.PermissionDenied)]
    [InlineData(1, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?", DockerError.DaemonUnavailable)]
    [InlineData(1, "Error response from daemon: No such container: web", DockerError.NoSuchContainer)]
    [InlineData(1, "Error response from daemon: conflict: unable to remove\nsecond line", DockerError.CommandFailed)]
    public void FailuresAreDescribedInTermsSomeoneCanActOn(int exitCode, string standardError, DockerError expected)
    {
        var failure = DockerCli.DescribeFailure(exitCode, standardError, "deploy");

        Assert.Equal(expected, failure.Error);
        Assert.DoesNotContain('\n', failure.Message);
        if (expected == DockerError.PermissionDenied)
        {
            Assert.Contains("usermod -aG docker deploy", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task HostListsContainersOverExec()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new FakeSshConnection { Execute = _ => new(0, _psOutput, string.Empty) };
        var host = new DockerCliHost(connection, "deploy");

        var listed = await host.ListContainersAsync(token);

        Assert.True(listed.IsSuccess);
        Assert.Equal(3, listed.Value.Count);
    }

    [Fact]
    public async Task HostReportsANonZeroExitAsADockerFailure()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new FakeSshConnection
        {
            Execute = _ => new(1, string.Empty, "Cannot connect to the Docker daemon at unix:///var/run/docker.sock."),
        };
        var host = new DockerCliHost(connection);

        var probe = await host.ProbeAsync(token);

        Assert.True(probe.IsFailure);
        Assert.Equal(DockerError.DaemonUnavailable, probe.Failure.Error);
    }

    [Fact]
    public async Task HostReportsABrokenChannelAsAConnectionFailure()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new FakeSshConnection();
        connection.FailNextExecute(SshError.ChannelClosed, "The SSH connection is closed.");
        var host = new DockerCliHost(connection);

        var result = await host.RunActionAsync("web", DockerAction.Restart, token);

        Assert.True(result.IsFailure);
        Assert.Equal(DockerError.ConnectionFailed, result.Failure.Error);
    }

    [Fact]
    public async Task HostSendsNothingForAnInvalidReference()
    {
        var token = TestContext.Current.CancellationToken;
        var sent = new List<string>();
        var connection = new FakeSshConnection
        {
            Execute = command =>
            {
                sent.Add(command);
                return new(0, string.Empty, string.Empty);
            },
        };
        var host = new DockerCliHost(connection);

        var action = await host.RunActionAsync("web; reboot", DockerAction.Stop, token);
        var logs = await host.OpenLogsAsync("$(reboot)", new DockerLogOptions(), token);

        Assert.Equal(DockerError.InvalidContainer, action.Failure.Error);
        Assert.Equal(DockerError.InvalidContainer, logs.Failure.Error);
        Assert.Empty(sent);
        Assert.Empty(connection.StartedCommands);
    }

    [Fact]
    public async Task UnreadableOutputIsAFailureRatherThanAnException()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new FakeSshConnection { Execute = _ => new(0, "{not json", string.Empty) };
        var host = new DockerCliHost(connection);

        var listed = await host.ListContainersAsync(token);

        Assert.Equal(DockerError.UnreadableOutput, listed.Failure.Error);
    }

    [Fact]
    public async Task LogsAreStreamedFromTheRunningCommand()
    {
        var token = TestContext.Current.CancellationToken;
        var connection = new FakeSshConnection
        {
            StartCommand = _ => FakeSshRunningCommand.Completed(0, "first", "second"),
        };
        var host = new DockerCliHost(connection);

        var opened = await host.OpenLogsAsync("web", new DockerLogOptions { Tail = 2 }, token);
        var lines = new List<string>();
        await foreach (var line in opened.Value.ReadLinesAsync(token))
        {
            lines.Add(line.Text);
        }

        Assert.Equal(["first", "second"], lines);
        Assert.Equal(0, opened.Value.ExitCode);
        Assert.Equal("docker logs --tail 2 'web'", Assert.Single(connection.StartedCommands));
    }
}
