using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.RegularExpressions;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Infrastructure.Pty;
using Xunit;

namespace RemoteFlow.Infrastructure.Tests;

public sealed partial class PortaPtyServiceTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ShellEchoFlowsThroughPipeReader()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new PortaPtyService();
        await using var session = await service.SpawnAsync(InteractiveShell(), token);

        await session.WriteAsync(Encoding.UTF8.GetBytes($"echo REMOTEFLOW_PTY_OK{NewLine()}"), token);
        var output = await ReadUntilAsync(
            session.Output,
            bytes => Encoding.UTF8.GetString(bytes).Contains("REMOTEFLOW_PTY_OK", StringComparison.Ordinal),
            token);

        Assert.Contains("REMOTEFLOW_PTY_OK", Encoding.UTF8.GetString(output), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalExitReportsCodeAndClosedFiresExactlyOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new PortaPtyService();
        await using var session = await service.SpawnAsync(InteractiveShell(), token);
        var closedCount = 0;
        ChannelClosedEventArgs? closed = null;
        session.Closed += (_, args) =>
        {
            _ = Interlocked.Increment(ref closedCount);
            closed = args;
        };

        var command = OperatingSystem.IsWindows() ? "exit /b 7\r\n" : "exit 7\n";
        await session.WriteAsync(Encoding.UTF8.GetBytes(command), token);
        var exitCode = await session.Exited.WaitAsync(_timeout, token);

        Assert.Equal(7, exitCode);
        Assert.Equal(1, Volatile.Read(ref closedCount));
        Assert.Equal(7, closed!.ExitCode);
        Assert.False(closed.WasKilled);
    }

    [Fact]
    public async Task DisposalKillsSessionAndRaisesClosedOnceWithNullExit()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new PortaPtyService();
        var session = await service.SpawnAsync(InteractiveShell(), token);
        var closedCount = 0;
        ChannelClosedEventArgs? closed = null;
        session.Closed += (_, args) =>
        {
            _ = Interlocked.Increment(ref closedCount);
            closed = args;
        };

        await session.DisposeAsync();
        var exitCode = await session.Exited.WaitAsync(_timeout, token);

        Assert.Null(exitCode);
        Assert.Equal(1, Volatile.Read(ref closedCount));
        Assert.True(closed!.WasKilled);
        await session.DisposeAsync();
        Assert.Equal(1, Volatile.Read(ref closedCount));
    }

    [Fact]
    public async Task ResizeIsObservableInsideChild()
    {
        var token = TestContext.Current.CancellationToken;
        var service = new PortaPtyService();
        var options = InteractiveShell() with { Columns = 80, Rows = 24 };
        await using var session = await service.SpawnAsync(options, token);

        await session.ResizeAsync(91, 33, token);
        var command = OperatingSystem.IsWindows()
            ? "mode con\r\n"
            : "stty size\n";
        await session.WriteAsync(Encoding.UTF8.GetBytes(command), token);
        var output = await ReadUntilAsync(
            session.Output,
            bytes =>
            {
                var text = Encoding.UTF8.GetString(bytes);
                return OperatingSystem.IsWindows()
                    ? text.Contains("91", StringComparison.Ordinal) && text.Contains("33", StringComparison.Ordinal)
                    : text.Contains("33 91", StringComparison.Ordinal);
            },
            token);
        var text = Encoding.UTF8.GetString(output);

        Assert.True(
            OperatingSystem.IsWindows()
                ? text.Contains("91", StringComparison.Ordinal) && text.Contains("33", StringComparison.Ordinal)
                : text.Contains("33 91", StringComparison.Ordinal),
            text);
    }

    [Fact]
    [Trait("Platform", "Windows")]
    public async Task DisposalKillsTheEntireWindowsProcessTreeByPid()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Job Object assertion.");
        var powerShell = ResolvePowerShell();
        Assert.SkipWhen(powerShell is null, "PowerShell is required for the process-tree probe.");
        var token = TestContext.Current.CancellationToken;
        var service = new PortaPtyService();
        await using var session = await service.SpawnAsync(new PtySpawnOptions
        {
            ShellPath = powerShell!,
            Arguments = ["-NoLogo", "-NoProfile", "-NoExit"],
            WorkingDirectory = Environment.CurrentDirectory,
        }, token);
        var parentPid = session.ProcessId;
        const string command = "$exe=(Get-Process -Id $PID).Path;$p=Start-Process -PassThru -WindowStyle Hidden -FilePath $exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 300'; Write-Output ('CHILD_PID=' + $p.Id)\r\n";
        await session.WriteAsync(Encoding.UTF8.GetBytes(command), token);
        var output = await ReadUntilAsync(
            session.Output,
            bytes => ChildPidRegex().IsMatch(Encoding.UTF8.GetString(bytes)),
            token);
        var match = ChildPidRegex().Match(Encoding.UTF8.GetString(output));
        var childPid = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        await session.DisposeAsync();

        Assert.True(await WaitForProcessExitAsync(parentPid, token), $"Parent PID {parentPid} is still alive.");
        Assert.True(await WaitForProcessExitAsync(childPid, token), $"Child PID {childPid} is still alive.");
    }

    [Fact]
    public async Task SplitUtf8WritesReachChildAsOneSequence()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The raw-byte probe currently uses PowerShell.");
        var powerShell = ResolvePowerShell();
        Assert.SkipWhen(powerShell is null, "PowerShell is required for the raw-byte probe.");
        var token = TestContext.Current.CancellationToken;
        var script = "$s=[Console]::OpenStandardInput();$b=New-Object byte[] 4;$o=0;while($o -lt 4){$n=$s.Read($b,$o,4-$o);if($n -eq 0){break};$o+=$n};[Console]::OpenStandardOutput().Write($b,0,$o)";
        var service = new PortaPtyService();
        await using var session = await service.SpawnAsync(new PtySpawnOptions
        {
            ShellPath = powerShell!,
            Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            WorkingDirectory = Environment.CurrentDirectory,
        }, token);
        var emoji = Encoding.UTF8.GetBytes("🙂");

        await session.WriteAsync(emoji.AsMemory(0, 2), token);
        await session.WriteAsync(emoji.AsMemory(2, 2), token);
        var output = await ReadUntilAsync(
            session.Output,
            bytes => bytes.AsSpan().IndexOf(emoji) >= 0,
            token);

        Assert.True(output.AsSpan().IndexOf(emoji) >= 0, Convert.ToHexString(output));
    }

    [Fact]
    public async Task AlreadyCancelledSpawnDoesNotCreateAProcess()
    {
        var service = new PortaPtyService();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SpawnAsync(InteractiveShell(), cancellation.Token));
    }

    private static PtySpawnOptions InteractiveShell()
    {
        return OperatingSystem.IsWindows()
            ? new PtySpawnOptions
            {
                ShellPath = Environment.GetEnvironmentVariable("ComSpec")
                    ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = ["/Q", "/D", "/K"],
                WorkingDirectory = Environment.CurrentDirectory,
                EnvironmentVariables = new Dictionary<string, string> { ["TERM"] = "xterm-256color" },
            }
            : new PtySpawnOptions
            {
                ShellPath = File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh",
                Arguments = File.Exists("/bin/bash") ? ["--noprofile", "--norc"] : [],
                WorkingDirectory = Environment.CurrentDirectory,
                EnvironmentVariables = new Dictionary<string, string> { ["TERM"] = "xterm-256color" },
            };
    }

    private static string NewLine()
    {
        return OperatingSystem.IsWindows() ? "\r\n" : "\n";
    }

    private static async Task<byte[]> ReadUntilAsync(
        PipeReader reader,
        Func<byte[], bool> completed,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var output = new ArrayBufferWriter<byte>();
        while (true)
        {
            var result = await reader.ReadAsync(timeout.Token);
            foreach (var segment in result.Buffer)
            {
                output.Write(segment.Span);
            }

            reader.AdvanceTo(result.Buffer.End);
            var bytes = output.WrittenSpan.ToArray();
            if (completed(bytes) || result.IsCompleted)
            {
                return bytes;
            }
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        return string.IsNullOrWhiteSpace(path)
            ? null
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(directory => Path.Combine(directory, fileName))
                .FirstOrDefault(File.Exists);
    }

    private static string? ResolvePowerShell()
    {
        var modern = FindOnPath("pwsh.exe");
        if (modern is not null)
        {
            return modern;
        }

        var windowsPowerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return File.Exists(windowsPowerShell) ? windowsPowerShell : null;
    }

    private static async Task<bool> WaitForProcessExitAsync(int processId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(50, cancellationToken);
        }

        return false;
    }

    [GeneratedRegex(@"CHILD_PID=(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ChildPidRegex();
}
