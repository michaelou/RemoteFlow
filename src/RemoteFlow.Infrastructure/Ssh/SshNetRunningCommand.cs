using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using RemoteFlow.Application.Abstractions.Ssh;
using Renci.SshNet;

namespace RemoteFlow.Infrastructure.Ssh;

/// <summary>A running exec channel read a line at a time. SSH.NET exposes standard output and standard error
/// as two separate pipe streams that only end when the channel closes, so each gets a pump and the two meet
/// in one channel — the order between them is arrival order at this end, which is the best either library
/// can offer.</summary>
internal sealed class SshNetRunningCommand : ISshRunningCommand
{
    private readonly SshCommand _command;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _execution;
    private int _enumerated;
    private int _disposed;

    private SshNetRunningCommand(SshCommand command)
    {
        _command = command;
        // Cancelling this token is how SSH.NET is asked to signal the remote command and close the channel.
        _execution = command.ExecuteAsync(_stop.Token);
    }

    public int? ExitCode { get; private set; }

    public SshFailure? Failure { get; private set; }

    /// <summary>Opening the channel is a blocking call in SSH.NET, so it runs off the caller's thread and
    /// under the caller's timeout.</summary>
    public static async Task<SshNetRunningCommand> StartAsync(
        SshClient client,
        string command,
        CancellationToken cancellationToken)
    {
        var sshCommand = client.CreateCommand(command);
        try
        {
            return await Task.Run(() => new SshNetRunningCommand(sshCommand), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            sshCommand.Dispose();
            throw;
        }
    }

    public async IAsyncEnumerable<SshOutputLine> ReadLinesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _enumerated, 1) != 0)
        {
            throw new InvalidOperationException("A command stream can be enumerated once.");
        }

        var lines = Channel.CreateUnbounded<SshOutputLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        var standardOutput = PumpAsync(_command.OutputStream, SshOutputKind.StandardOutput, lines.Writer);
        var standardError = PumpAsync(_command.ExtendedOutputStream, SshOutputKind.StandardError, lines.Writer);
        _ = Task.WhenAll(standardOutput, standardError).ContinueWith(
            static (_, state) => ((ChannelWriter<SshOutputLine>)state!).TryComplete(),
            lines.Writer,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        await foreach (var line in lines.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return line;
        }

        try
        {
            await _execution.ConfigureAwait(false);
            ExitCode = _command.ExitStatus;
        }
        catch (Exception) when (Volatile.Read(ref _disposed) != 0)
        {
        }
        catch (Exception exception)
        {
            Failure = SshErrorMapper.Failure<object>(exception, CancellationToken.None).Failure;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _execution.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The cancellation this method just requested surfaces here as an exception; there is nothing
            // further to report once the caller has asked for the stream to stop.
        }

        _command.Dispose();
        _stop.Dispose();
    }

    private static Task PumpAsync(Stream source, SshOutputKind kind, ChannelWriter<SshOutputLine> sink)
    {
        // PipeStream reads block until data arrives or the channel closes, so the pumps run on the pool
        // rather than borrowing whatever thread enumerated the stream.
        return Task.Run(async () =>
        {
            using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    _ = sink.TryWrite(new SshOutputLine(kind, line));
                }
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // The channel closed under the reader; the other pump and the execution task say why.
            }
        });
    }
}
