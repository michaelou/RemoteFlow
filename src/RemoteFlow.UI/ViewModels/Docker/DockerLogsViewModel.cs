using System.Collections.ObjectModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteFlow.Application.Abstractions;
using RemoteFlow.Application.Abstractions.Docker;
using RemoteFlow.Application.Abstractions.Ssh;

namespace RemoteFlow.UI.ViewModels.Docker;

/// <summary>One line of a container's log. Lines the container wrote to standard error are kept apart so
/// the view can mark them.</summary>
public sealed record DockerLogLine(string Text, bool IsError);

/// <summary>The log pane under the container list: the last few hundred lines of one container, optionally
/// followed as it writes more.
///
/// Lines are read off the SSH channel on the thread pool and handed to the list in batches, a few times a
/// second at most, so a chatty container costs a handful of layout passes rather than one per line. The
/// pane keeps the newest <see cref="MaxLines"/>; the server keeps the rest.</summary>
public sealed partial class DockerLogsViewModel(IClipboardService? clipboard = null) : ObservableObject, IAsyncDisposable
{
    private readonly List<DockerLogLine> _all = [];
    private IDockerHost? _host;
    private string? _containerId;
    private ISshRunningCommand? _command;
    private CancellationTokenSource? _reading;

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
    public partial string? ContainerName { get; private set; }

    [ObservableProperty]
    public partial bool Follow { get; set; } = true;

    [ObservableProperty]
    public partial bool Timestamps { get; set; }

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsStreaming { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>Raised after a batch is added, so the view can keep the newest line in sight while following.</summary>
    public event EventHandler? LinesAppended;

    public string Heading => ContainerName is null ? "Logs" : $"Logs — {ContainerName}";

    public async Task OpenAsync(IDockerHost host, string containerId, string containerName)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        await StopAsync().ConfigureAwait(true);
        _host = host;
        _containerId = containerId;
        ContainerName = containerName;
        OnPropertyChanged(nameof(Heading));
        IsOpen = true;
        await StartAsync().ConfigureAwait(true);
    }

    /// <summary>Hides the pane and stops reading, which closes the channel and ends a followed
    /// <c>docker logs</c> on the server.</summary>
    [RelayCommand]
    public async Task CloseAsync()
    {
        await StopAsync().ConfigureAwait(true);
        _host = null;
        _containerId = null;
        ContainerName = null;
        OnPropertyChanged(nameof(Heading));
        IsOpen = false;
        StatusMessage = null;
        ClearLines();
    }

    [RelayCommand]
    public Task ReloadAsync()
    {
        return _host is null ? Task.CompletedTask : RestartAsync();
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
        _ = RestartIfOpenAsync();
    }

    partial void OnTimestampsChanged(bool value)
    {
        _ = RestartIfOpenAsync();
    }

    partial void OnFilterTextChanged(string value)
    {
        Lines.Clear();
        foreach (var line in _all.Where(Matches))
        {
            Lines.Add(line);
        }
    }

    private Task RestartIfOpenAsync()
    {
        return _host is null ? Task.CompletedTask : RestartAsync();
    }

    private async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(true);
        await StartAsync().ConfigureAwait(true);
    }

    private async Task StartAsync()
    {
        if (_host is null || _containerId is null)
        {
            return;
        }

        ClearLines();
        StatusMessage = Follow ? "Following…" : "Loading…";
        var reading = new CancellationTokenSource();
        _reading = reading;
        var opened = await _host.OpenLogsAsync(
            _containerId,
            new DockerLogOptions { Tail = Tail, Follow = Follow, Timestamps = Timestamps },
            reading.Token).ConfigureAwait(true);
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
    }

    private async Task PumpAsync(ISshRunningCommand command, CancellationToken cancellationToken)
    {
        var pending = Channel.CreateUnbounded<DockerLogLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in command.ReadLinesAsync(cancellationToken).ConfigureAwait(false))
                {
                    _ = pending.Writer.TryWrite(new DockerLogLine(line.Text, line.Kind == SshOutputKind.StandardError));
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
        StatusMessage = command.Failure is { } failure
            ? $"The log stream broke: {failure.Message}"
            : command.ExitCode is { } exitCode and not 0 && received == 0
                ? $"docker logs exited with code {exitCode}."
                : Follow
                    ? "The log stream ended; the container may have stopped."
                    : null;
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

        if (IsStreaming && Follow)
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
