using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Porta.Pty;
using SvcSystems.UI.Terminal;

namespace RemoteFlow.TerminalSpike;

public partial class MainWindow : Window
{
    private readonly SpikeLaunchOptions _launchOptions;
    private readonly TerminalControlModel _terminalModel = new(new TerminalOptions
    {
        Cols = 120,
        Rows = 30,
        Scrollback = 10_000,
        ReflowOnResize = false,
        TermName = "xterm-256color",
    });
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private readonly DispatcherTimer _metricsTimer;
    private readonly DispatcherTimer _uiPulseTimer;
    private readonly Stopwatch _sessionClock = new();

    private IPtyConnection? _pty;
    private CancellationTokenSource? _readCancellation;
    private long _bytesRead;
    private long _feedCount;
    private double _totalFeedMilliseconds;
    private double _maxFeedMilliseconds;
    private long _pulseCount;
    private double _totalPulseMilliseconds;
    private double _maxPulseMilliseconds;
    private long _lastPulseTimestamp;
    private string _lastInputBytes = "(none)";
    private string _sessionState = "starting";
    private bool _stopping;

    public MainWindow()
        : this(App.LaunchOptions)
    {
    }

    private MainWindow(SpikeLaunchOptions launchOptions)
    {
        _launchOptions = launchOptions;
        InitializeComponent();

        TerminalView.Model = _terminalModel;
        _terminalModel.UserInput += TerminalModel_OnUserInput;
        _terminalModel.SizeChanged += TerminalModel_OnSizeChanged;

        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _metricsTimer.Tick += (_, _) => UpdateMetricsText();

        _uiPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _uiPulseTimer.Tick += UiPulseTimer_OnTick;

        Opened += async (_, _) => await StartShellAsync();
        Closed += (_, _) => StopShell();
    }

    private async Task StartShellAsync()
    {
        StopShell();
        ResetMetrics();
        _stopping = false;
        _sessionState = "starting";
        UpdateMetricsText();

        var environment = new Dictionary<string, string>
        {
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = _launchOptions.ColorMode == "truecolor" ? "truecolor" : string.Empty,
        };

        try
        {
            _readCancellation = new CancellationTokenSource();
            _pty = await PtyProvider.SpawnAsync(new PtyOptions
            {
                Name = "RemoteFlow.TerminalSpike",
                Cols = Math.Max(_terminalModel.Terminal.Cols, 1),
                Rows = Math.Max(_terminalModel.Terminal.Rows, 1),
                Cwd = _launchOptions.WorkingDirectory,
                App = _launchOptions.Shell,
                CommandLine = _launchOptions.Arguments,
                Environment = environment,
            }, _readCancellation.Token);

            _pty.ProcessExited += Pty_OnProcessExited;
            _sessionState = $"running (pid {_pty.Pid})";
            _sessionClock.Start();
            _metricsTimer.Start();
            _lastPulseTimestamp = Stopwatch.GetTimestamp();
            _uiPulseTimer.Start();
            _ = ReadPtyAsync(_pty, _readCancellation.Token);
        }
        catch (Exception exception)
        {
            _sessionState = "failed to start";
            _terminalModel.Feed($"\r\n[PTY start failed: {exception}]\r\n");
            UpdateMetricsText();
        }
    }

    private async Task ReadPtyAsync(IPtyConnection connection, CancellationToken cancellationToken)
    {
        var buffer = new byte[_launchOptions.ReadBufferSize];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await connection.ReaderStream.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                Interlocked.Add(ref _bytesRead, read);
                var chunk = buffer.AsSpan(0, read).ToArray();
                await Dispatcher.UIThread.InvokeAsync(() => FeedAndMeasure(chunk), DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _sessionState = "read failed";
                _terminalModel.Feed($"\r\n[PTY read failed: {exception.Message}]\r\n");
            });
        }
    }

    private void FeedAndMeasure(byte[] data)
    {
        var started = Stopwatch.GetTimestamp();
        _terminalModel.Feed(data, data.Length);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        _feedCount++;
        _totalFeedMilliseconds += elapsed;
        _maxFeedMilliseconds = Math.Max(_maxFeedMilliseconds, elapsed);
    }

    private async void TerminalModel_OnUserInput(object? sender, TerminalUserInputEventArgs e)
    {
        var connection = _pty;
        if (connection is null || e.Data.IsEmpty)
        {
            return;
        }

        _lastInputBytes = Convert.ToHexString(e.Data.Span);
        await _writerGate.WaitAsync();
        try
        {
            await connection.WriterStream.WriteAsync(e.Data);
            await connection.WriterStream.FlushAsync();
        }
        catch (Exception exception) when (!_stopping)
        {
            _sessionState = $"write failed: {exception.Message}";
        }
        finally
        {
            _writerGate.Release();
        }
    }

    private void TerminalModel_OnSizeChanged(object? sender, TerminalSizeChangedEventArgs e)
    {
        try
        {
            _pty?.Resize(Math.Max(e.Cols, 1), Math.Max(e.Rows, 1));
        }
        catch (Exception exception) when (!_stopping)
        {
            _sessionState = $"resize failed: {exception.Message}";
        }
    }

    private void Pty_OnProcessExited(object? sender, PtyExitedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _sessionState = $"exited ({e.ExitCode})";
            _terminalModel.Feed($"\r\n[Shell exited with code {e.ExitCode}]\r\n");
            UpdateMetricsText();
        });
    }

    private void StopShell()
    {
        _stopping = true;
        _metricsTimer?.Stop();
        _uiPulseTimer?.Stop();
        _sessionClock.Stop();
        _readCancellation?.Cancel();
        _readCancellation?.Dispose();
        _readCancellation = null;

        if (_pty is not null)
        {
            _pty.ProcessExited -= Pty_OnProcessExited;
            try
            {
                _pty.Kill();
            }
            catch
            {
                // The shell may already have exited.
            }

            _pty.Dispose();
            _pty = null;
        }
    }

    private void UiPulseTimer_OnTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastPulseTimestamp != 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(_lastPulseTimestamp, now).TotalMilliseconds;
            _pulseCount++;
            _totalPulseMilliseconds += elapsed;
            _maxPulseMilliseconds = Math.Max(_maxPulseMilliseconds, elapsed);
        }

        _lastPulseTimestamp = now;
    }

    private void ResetMetrics()
    {
        _sessionClock.Reset();
        _bytesRead = 0;
        _feedCount = 0;
        _totalFeedMilliseconds = 0;
        _maxFeedMilliseconds = 0;
        _pulseCount = 0;
        _totalPulseMilliseconds = 0;
        _maxPulseMilliseconds = 0;
        _lastPulseTimestamp = 0;
        _lastInputBytes = "(none)";
    }

    private void UpdateMetricsText()
    {
        var process = Process.GetCurrentProcess();
        var elapsedSeconds = Math.Max(_sessionClock.Elapsed.TotalSeconds, 0.001);
        var mibibytes = _bytesRead / 1024d / 1024d;
        var meanFeed = _feedCount == 0 ? 0 : _totalFeedMilliseconds / _feedCount;
        var meanPulse = _pulseCount == 0 ? 0 : _totalPulseMilliseconds / _pulseCount;

        SessionText.Text =
            $"{_sessionState} | {_launchOptions.Shell} {string.Join(' ', _launchOptions.Arguments)} | " +
            $"color={_launchOptions.ColorMode} | read buffer={_launchOptions.ReadBufferSize} B | " +
            $"size={_terminalModel.Terminal.Cols}x{_terminalModel.Terminal.Rows}";
        MetricsText.Text =
            $"read={mibibytes:F2} MiB ({mibibytes / elapsedSeconds:F2} MiB/s) | " +
            $"feed mean/max={meanFeed:F2}/{_maxFeedMilliseconds:F2} ms | " +
            $"UI pulse mean/max={meanPulse:F2}/{_maxPulseMilliseconds:F2} ms | " +
            $"working/peak={ToMib(process.WorkingSet64):F1}/{ToMib(process.PeakWorkingSet64):F1} MiB | " +
            $"scrollback={_terminalModel.Terminal.Buffer.Lines.Length} lines | last input bytes={_lastInputBytes}";
    }

    private static double ToMib(long bytes) => bytes / 1024d / 1024d;

    private async void RestartShell_OnClick(object? sender, RoutedEventArgs e) => await StartShellAsync();

    private async void ExportEvidence_OnClick(object? sender, RoutedEventArgs e)
    {
        var process = Process.GetCurrentProcess();
        var evidence = new
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Platform = Environment.OSVersion.ToString(),
            Runtime = Environment.Version.ToString(),
            Shell = _launchOptions.Shell,
            _launchOptions.Arguments,
            _launchOptions.ColorMode,
            _launchOptions.ReadBufferSize,
            SessionState = _sessionState,
            Terminal = new
            {
                _terminalModel.Terminal.Cols,
                _terminalModel.Terminal.Rows,
                ScrollbackLines = _terminalModel.Terminal.Buffer.Lines.Length,
                _terminalModel.Terminal.IsAlternateBufferActive,
            },
            Metrics = new
            {
                BytesRead = _bytesRead,
                DurationSeconds = _sessionClock.Elapsed.TotalSeconds,
                FeedCount = _feedCount,
                MeanFeedMilliseconds = _feedCount == 0 ? 0 : _totalFeedMilliseconds / _feedCount,
                MaxFeedMilliseconds = _maxFeedMilliseconds,
                MeanUiPulseMilliseconds = _pulseCount == 0 ? 0 : _totalPulseMilliseconds / _pulseCount,
                MaxUiPulseMilliseconds = _maxPulseMilliseconds,
                WorkingSetBytes = process.WorkingSet64,
                PeakWorkingSetBytes = process.PeakWorkingSet64,
            },
            LastInputBytes = _lastInputBytes,
        };

        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "terminal-spike");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"metrics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        _sessionState = $"evidence exported to {path}";
        UpdateMetricsText();
    }

    private void Find_OnClick(object? sender, RoutedEventArgs e)
    {
        var count = TerminalView.Search(SearchTextBox.Text ?? string.Empty);
        SearchStatusText.Text = count == 0 ? "No matches" : $"1 / {count}";
    }

    private void FindNext_OnClick(object? sender, RoutedEventArgs e)
    {
        var index = TerminalView.SelectNextSearchResult();
        SearchStatusText.Text = index < 0 ? "No matches" : $"{index + 1} / {_terminalModel.SearchResultCount}";
    }

    private async void Copy_OnClick(object? sender, RoutedEventArgs e) => await TerminalView.CopySelectionAsync();

    private async void Paste_OnClick(object? sender, RoutedEventArgs e) => await TerminalView.PasteFromClipboardAsync();

    private void Utf8Probe_OnClick(object? sender, RoutedEventArgs e)
    {
        _terminalModel.Feed("split boundary: ");
        var bytes = Encoding.UTF8.GetBytes("漢字 e\u0301 ┌─┐ 🙂\r\n");
        _terminalModel.Feed(bytes[..1], 1);
        _terminalModel.Feed(bytes[1..], bytes.Length - 1);
    }
}
