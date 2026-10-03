using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Abstractions.Ssh;

namespace RemoteFlow.UI.ViewModels.Docker;

/// <summary>One line of a container's log, or of an operation's output. Lines written to standard error are
/// kept apart so the view can mark them.</summary>
public sealed record DockerLogLine(string Text, bool IsError);

/// <summary>How an operation shown in the pane ended.</summary>
public sealed record DockerOperationOutcome(bool Succeeded, string Message);

/// <summary>The output pane under the Docker page's lists. It shows one of two things: the last few hundred
/// lines of a container's log, optionally followed as it writes more; or the output of an operation that
/// can run for minutes — a compose up that pulls, an image pull, a prune — as it happens.
///
/// Lines are read off the SSH channel on the thread pool and handed to the list in batches, a few times a
/// second at most, so a chatty container costs a handful of layout passes rather than one per line. The
/// pane keeps the newest <see cref="MaxLines"/>; the server keeps the rest.
///
/// An operation cannot be closed or replaced while it runs: closing the pane closes the channel, and a
/// <c>docker compose up</c> cut off halfway leaves a project half up.</summary>
public sealed partial class DockerLogsViewModel(IClipboardService? clipboard = null) : ObservableObject, IAsyncDisposable
{
    private readonly List<DockerLogLine> _all = [];
    private Func<CancellationToken, Task<DockerResult<ISshRunningCommand>>>? _start;
    private ISshRunningCommand? _command;
    private CancellationTokenSource? _reading;
    private TaskCompletionSource<DockerOperationOutcome>? _operation;

    public const int MaxLines = 5000;

    /// <summary>The lines that pass the filter, oldest first.</summary>
    public ObservableCollection<DockerLogLine> Lines { get; } = [];

    /// <summary>How long the pump gathers lines before handing them to the list. Tests set it to zero.</summary>
    public TimeSpan BatchInterval { get; set; } = TimeSpan.FromMilliseconds(150);

    public int Tail { get; set; } = 500;

    /// <summary>Completes when the current read has ended, for whatever reason.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string Heading { get; private set; } = "Logs";

    /// <summary>True while the pane shows an operation rather than a log: the log-only controls hide.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLog))]
    [NotifyPropertyChangedFor(nameof(IsOperationRunning))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    public partial bool IsOperation { get; private set; }

    [ObservableProperty]
    public partial bool Follow { get; set; } = true;

    [ObservableProperty]
    public partial bool Timestamps { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOperationRunning))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    public partial bool IsStreaming { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    public bool IsLog => !IsOperation;

    public bool IsOperationRunning => IsOperation && IsStreaming;

    /// <summary>Raised after a batch is added, so the view can keep the newest line in sight while following.</summary>
    public event EventHandler? LinesAppended;

    /// <summary>Shows a container's log. Refused — returning false — while an operation is running.</summary>
    public async Task<bool> OpenAsync(IDockerHost host, string containerId, string containerName)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        if (IsOperationRunning)
        {
            return false;
        }

        await StopAsync().ConfigureAwait(true);
        _start = cancellationToken => host.OpenLogsAsync(
            containerId,
            new DockerLogOptions { Tail = Tail, Follow = Follow, Timestamps = Timestamps },
            cancellationToken);
        IsOperation = false;
        Heading = $"Logs — {containerName}";
        IsOpen = true;
        await StartAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>Runs an operation in the pane and completes when it has ended, with how it ended. Null when
    /// another operation is still running, since cutting that one off is never what was meant.</summary>
    public async Task<DockerOperationOutcome?> RunOperationAsync(
        string heading,
        Func<CancellationToken, Task<DockerResult<ISshRunningCommand>>> start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heading);
        ArgumentNullException.ThrowIfNull(start);
        if (IsOperationRunning)
        {
            return null;
        }

        await StopAsync().ConfigureAwait(true);
        var operation = new TaskCompletionSource<DockerOperationOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = operation;
        _start = start;
        IsOperation = true;
        Heading = heading;
        IsOpen = true;
        await StartAsync().ConfigureAwait(true);
        return await operation.Task.ConfigureAwait(true);
    }

    /// <summary>Hides the pane and stops reading, which closes the channel and ends a followed
    /// <c>docker logs</c> on the server. Not available while an operation runs.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    public async Task CloseAsync()
    {
        await StopAsync().ConfigureAwait(true);
        _start = null;
        Heading = "Logs";
        IsOperation = false;
        IsOpen = false;
        StatusMessage = null;
        ClearLines();
    }

    [RelayCommand]
    public Task ReloadAsync()
    {
        return _start is null || IsOperation ? Task.CompletedTask : RestartAsync();
    }

    [RelayCommand]
    public void Clear()
    {
        ClearLines();
    }

    [RelayCommand]
    public async Task CopyAllAsync()
    {
        if (clipboard is null || Lines.Count == 0)
        {
            return;
        }

        var result = await clipboard.WriteTextAsync(string.Join(Environment.NewLine, Lines.Select(line => line.Text)))
            .ConfigureAwait(true);
        StatusMessage = result.Succeeded
            ? $"Copied {Lines.Count} {(Lines.Count == 1 ? "line" : "lines")}."
            : result.ErrorMessage;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    partial void OnFollowChanged(bool value)
    {
        _ = RestartIfLogAsync();
    }

    partial void OnTimestampsChanged(bool value)
    {
        _ = RestartIfLogAsync();
    }

    partial void OnFilterTextChanged(string value)
    {
        Lines.Clear();
        foreach (var line in _all.Where(Matches))
        {
            Lines.Add(line);
        }
    }

    private bool CanClose()
    {
        return !IsOperationRunning;
    }

    private Task RestartIfLogAsync()
    {
        return _start is null || IsOperation ? Task.CompletedTask : RestartAsync();
    }

    private async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(true);
        await StartAsync().ConfigureAwait(true);
    }

    private async Task StartAsync()
    {
        if (_start is null)
        {
            return;
        }

        ClearLines();
        StatusMessage = IsOperation ? "Running…" : Follow ? "Following…" : "Loading…";
        var reading = new CancellationTokenSource();
        _reading = reading;
        var opened = await _start(reading.Token).ConfigureAwait(true);
        if (!ReferenceEquals(reading, _reading))
        {
            if (opened.IsSuccess)
            {
                await opened.Value.DisposeAsync().ConfigureAwait(true);
            }

            return;
        }

        if (opened.IsFailure)
        {
            StatusMessage = opened.Failure.Message;
            FinishOperation(new DockerOperationOutcome(false, opened.Failure.Message));
            return;
        }

        _command = opened.Value;
        IsStreaming = true;
        Completion = PumpAsync(opened.Value, reading.Token);
    }

    private async Task StopAsync()
    {
        var reading = _reading;
        _reading = null;
        if (reading is not null)
        {
            await reading.CancelAsync().ConfigureAwait(true);
        }

        var command = _command;
        _command = null;
        if (command is not null)
        {
            await command.DisposeAsync().ConfigureAwait(true);
        }

        try
        {
            await Completion.ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        reading?.Dispose();
        IsStreaming = false;
        FinishOperation(new DockerOperationOutcome(false, "The operation was stopped."));
    }

    private async Task PumpAsync(ISshRunningCommand command, CancellationToken cancellationToken)
    {
        var pending = Channel.CreateUnbounded<DockerLogLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        // Compose and pull write their progress to standard error, so for an operation the stream says
        // nothing about trouble: the exit code does. Only a container's own standard error is marked.
        var markErrors = !IsOperation;
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in command.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
                {
                    _ = pending.Writer.TryWrite(new DockerLogLine(
                        line.Text,
                        markErrors && line.Kind == SshOutputKind.StandardError));
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _ = pending.Writer.TryComplete();
            }
        }, CancellationToken.None);

        var batch = new List<DockerLogLine>();
        var received = 0;
        try
        {
            while (await pending.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(true))
            {
                while (pending.Reader.TryRead(out var line))
                {
                    batch.Add(line);
                }

                received += batch.Count;
                Append(batch);
                batch.Clear();
                if (BatchInterval > TimeSpan.Zero)
                {
                    await Task.Delay(BatchInterval, cancellationToken).ConfigureAwait(true);
                }
            }

            await reader.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        IsStreaming = false;
        if (IsOperation)
        {
            var outcome = command.Failure is { } broken
                ? new DockerOperationOutcome(false, $"The connection broke: {broken.Message}")
                : command.ExitCode is 0
                    ? new DockerOperationOutcome(true, "Finished.")
                    : new DockerOperationOutcome(false, command.ExitCode is { } code
                        ? $"Failed: exited with code {code}."
                        : "Ended without an exit code.");
            StatusMessage = outcome.Message;
            FinishOperation(outcome);
            return;
        }

        StatusMessage = command.Failure is { } failure
            ? $"The log stream broke: {failure.Message}"
            : command.ExitCode is { } exitCode and not 0 && received == 0
                ? $"docker logs exited with code {exitCode}."
                : Follow
                    ? "The log stream ended; the container may have stopped."
                    : null;
    }

    private void FinishOperation(DockerOperationOutcome outcome)
    {
        var operation = _operation;
        _operation = null;
        _ = operation?.TrySetResult(outcome);
    }

    private void Append(List<DockerLogLine> batch)
    {
        if (batch.Count == 0)
        {
            return;
        }

        _all.AddRange(batch);
        if (_all.Count > MaxLines)
        {
            _all.RemoveRange(0, _all.Count - MaxLines);
        }

        foreach (var line in batch.Where(Matches))
        {
            Lines.Add(line);
        }

        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }

        if (IsStreaming && Follow && !IsOperation)
        {
            StatusMessage = "Following…";
        }

        LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    private bool Matches(DockerLogLine line)
    {
        return FilterText.Length == 0 || line.Text.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private void ClearLines()
    {
        _all.Clear();
        Lines.Clear();
    }
}
