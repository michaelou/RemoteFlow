using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RemoteFlow.RdpSpike.Diagnostics;
using RemoteFlow.RdpSpike.Hosting;
using RemoteFlow.RdpSpike.Interop;
using RemoteFlow.RdpSpike.Rdp;

namespace RemoteFlow.RdpSpike;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions _jsonSerializerOptions = new() { WriteIndented = true };

    private readonly SpikeOptions _launchOptions;
    private readonly StringBuilder _log = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly List<string> _events = [];
    private readonly Lazy<string> _logPath = new(() =>
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "rdp-spike");
        _ = Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");
    });

    private IReadOnlyList<RdpClassProbe> _probes = [];
    private KeyboardProbe? _keyboard;
    private IActiveXContainer? _container;
    private RdpNativeHost? _host;
    private RdpSession? _session;
    private RdpEventSink? _sink;
    private RdpClassProbe? _activeClass;
    private double _lastScaling;
    private string _state = "idle";

    public MainWindow()
        : this(App.LaunchOptions)
    {
    }

    private MainWindow(SpikeOptions launchOptions)
    {
        _launchOptions = launchOptions;
        InitializeComponent();

        HostTextBox.Text = launchOptions.Host;
        UserTextBox.Text = launchOptions.UserName;
        ContainerComboBox.SelectedIndex = launchOptions.UseWinFormsContainer ? 1 : 0;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        Opened += MainWindow_OnOpened;
        Closing += (_, _) => TearDown();
    }

    private void MainWindow_OnOpened(object? sender, EventArgs e)
    {
        // Avalonia's Win32 backend initialises OLE for drag and drop, so this is expected to return
        // S_FALSE. Logging it either way records whether the spike relies on that or does it itself.
        Log($"OleInitialize -> 0x{Win32.OleInitialize(IntPtr.Zero):X8}");
        Log($"process architecture = {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, " +
            $"OS = {Environment.OSVersion.VersionString}");

        _probes = RdpClassChain.ProbeAll();
        foreach (var probe in _probes)
        {
            Log(probe.Summary);
        }

        List<string> classChoices = ["auto"];
        classChoices.AddRange(_probes
            .Where(probe => probe.Activated)
            .Select(probe => probe.Candidate.Generation.ToString(CultureInfo.InvariantCulture)));
        ClassComboBox.ItemsSource = classChoices;
        ClassComboBox.SelectedIndex = 0;

        try
        {
            _keyboard = new KeyboardProbe();
            Log("keyboard probe installed on the UI thread (WH_GETMESSAGE)");
        }
        catch (InvalidOperationException exception)
        {
            Log($"keyboard probe unavailable: {exception.Message}");
        }

        _lastScaling = RenderScaling;
        _statusTimer.Start();
        UpdateStatus();

        if (_launchOptions.Auto)
        {
            BuildAutoScript().Start();
        }
    }

    /// <summary>The scripted run behind `--auto`. Each step maps to one of the eight questions and its
    /// answer is whatever the log says happened, not what the step hoped for.</summary>
    private AutoScript BuildAutoScript()
    {
        var script = new AutoScript(Log);
        return script
            .Then(0.5, "connect", () => Connect_OnClick(null, new RoutedEventArgs()))
            .Then(3.0, "page B with the host kept attached (IsVisible only)", () =>
            {
                KeepAttachedSwitch.IsChecked = true;
                ShowPage(showA: false);
            })
            .Then(4.0, "back to page A", () => ShowPage(showA: true))
            .Then(5.0, "page B by detaching the host from the visual tree", () =>
            {
                KeepAttachedSwitch.IsChecked = false;
                ShowPage(showA: false);
            })
            .Then(6.0, "back to page A, re-attaching the host", () => ShowPage(showA: true))
            .Then(7.0, "select the second tab, which detaches the whole page", () => Tabs.SelectedIndex = 1)
            .Then(8.5, "select the session tab again", () => Tabs.SelectedIndex = 0)
            .Then(9.5, "connected after the round trip?", () =>
                Log($"after tab round trip: state={_session?.ConnectionState ?? "no session"}, " +
                    $"attach/detach={_host?.AttachCount ?? 0}/{_host?.DetachCount ?? 0}, " +
                    $"control window alive={Win32.IsWindow(_container?.Handle ?? IntPtr.Zero)}"))
            .Then(10.0, "resize via UpdateSessionDisplaySettings", () =>
                ResizeUpdate_OnClick(null, new RoutedEventArgs()))
            .Then(10.5, "resize via Reconnect", () => ResizeReconnect_OnClick(null, new RoutedEventArgs()))
            .Then(11.0, "focus the control", () =>
            {
                FocusControl_OnClick(null, new RoutedEventArgs());
                var ole = _container as OleSiteContainer;
                Log($"keyboard: {_keyboard?.Summary ?? "probe off"}; " +
                    $"site TranslateAccelerator calls={ole?.TranslateAcceleratorCalls.ToString(CultureInfo.InvariantCulture) ?? "n/a"}, " +
                    $"site OnFocus calls={ole?.OnFocusCalls.ToString(CultureInfo.InvariantCulture) ?? "n/a"}");
            })
            .Then(12.0, "disconnect", () => Disconnect_OnClick(null, new RoutedEventArgs()))
            .Then(13.5, "reuse the same instance: connect again without destroying it", () =>
                Log($"reuse: Connect -> {_session?.Connect()}"))
            .Then(16.0, "destroy the control and build a fresh one", () =>
            {
                Destroy_OnClick(null, new RoutedEventArgs());
                Connect_OnClick(null, new RoutedEventArgs());
            })
            .Then(19.0, "export evidence", () => ExportEvidence_OnClick(null, new RoutedEventArgs()))
            .Then(20.0, "done", () =>
            {
                if (_launchOptions.ExitWhenDone)
                {
                    Close();
                }
            });
    }

    private void Connect_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_session is not null)
        {
            Log("already have a control; use Destroy control first to test a fresh instance");
            _ = _session.Connect();
            return;
        }

        var (host, port) = SplitHost(HostTextBox.Text ?? string.Empty);
        if (string.IsNullOrWhiteSpace(host))
        {
            Log("no host: type one in, or pass --host. The control needs a Server before Connect.");
            return;
        }

        var pinned = ClassComboBox.SelectedIndex > 0 && int.TryParse(
            ClassComboBox.SelectedItem?.ToString(),
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : _launchOptions.PinnedGeneration;

        _activeClass = RdpClassChain.Resolve(_probes, pinned);
        if (_activeClass is null)
        {
            Log("no class in the chain activated; nothing to host");
            return;
        }

        Log($"using {_activeClass.Candidate.CoClassName} {_activeClass.Candidate.ClassId:B}");

        var useWinForms = ContainerComboBox.SelectedIndex == 1;
        _container = useWinForms ? new WinFormsAxContainer() : new OleSiteContainer();
        Log($"container = {_container.Name}");

        var (targetWidth, targetHeight) = TargetPixelSize();
        try
        {
            _container.Create(_activeClass.Candidate.ClassId, targetWidth, targetHeight);
        }
        catch (InvalidOperationException exception)
        {
            Log($"container creation failed: {exception.Message}");
            _container.Dispose();
            _container = null;
            return;
        }

        foreach (var note in _container.Notes)
        {
            Log($"  {note}");
        }

        var control = _container.Control;
        if (control is null)
        {
            Log("the container produced no control object");
            return;
        }

        _sink = new RdpEventSink();
        _sink.Raised += Sink_OnRaised;
        var advise = _sink.Advise(control);
        Log(string.IsNullOrEmpty(advise) ? "event sink advised on IMsTscAxEvents" : $"event sink: {advise}");

        _session = RdpSession.Create(control, Log);
        if (_session is null)
        {
            Log("no IMsRdpClient10 on this class; the spike cannot drive it");
            return;
        }

        Log($"control version = {_session.Version ?? "unknown"}");
        var (desktopScale, deviceScale) = ScaleFactors();
        _session.Configure(
            _launchOptions with { Host = host, Port = port, UserName = UserTextBox.Text ?? string.Empty },
            targetWidth,
            targetHeight,
            desktopScale,
            deviceScale);

        _host = new RdpNativeHost();
        _host.LifecycleChanged += (_, message) => Log(message);
        _host.Attach(_container);
        SessionSlot.Content = _host;

        _keyboard?.Watch(_container.Handle);
        _state = "connecting";
        _ = _session.Connect();
        Log($"Connect issued for {host}:{port}");
    }

    private void Disconnect_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            Log("no session");
            return;
        }

        _ = _session.Disconnect();
    }

    /// <summary>Question 7: whether a control instance can be reused, or whether reconnect has to build a
    /// fresh one. Destroying here and connecting again is the comparison.</summary>
    private void Destroy_OnClick(object? sender, RoutedEventArgs e)
    {
        SessionSlot.Content = null;
        _host?.Release();
        _host = null;

        _session?.Dispose();
        _session = null;

        _sink?.Dispose();
        _sink = null;

        _container?.Dispose();
        _container = null;

        _state = "control destroyed";
        Log("control destroyed; Connect will build a fresh instance");
    }

    private void ResizeReconnect_OnClick(object? sender, RoutedEventArgs e)
    {
        var (width, height) = TargetPixelSize();
        _ = _session?.Reconnect(width, height);
    }

    private void ResizeUpdate_OnClick(object? sender, RoutedEventArgs e)
    {
        var (width, height) = TargetPixelSize();
        var (desktopScale, deviceScale) = ScaleFactors();
        _ = _session?.UpdateSessionDisplaySettings(width, height, desktopScale, deviceScale);
    }

    private void FocusControl_OnClick(object? sender, RoutedEventArgs e)
    {
        Log(_container?.FocusControl() == true
            ? "focus landed inside the control"
            : "focus did not land inside the control");
    }

    private void ShowPageA_OnClick(object? sender, RoutedEventArgs e)
    {
        ShowPage(showA: true);
    }

    private void ShowPageB_OnClick(object? sender, RoutedEventArgs e)
    {
        ShowPage(showA: false);
    }

    /// <summary>The two strategies from question 4, side by side. Keeping the view attached and toggling
    /// IsVisible never detaches the host; taking it out of the tree does, and the offscreen holder is what
    /// keeps the session alive when it happens.</summary>
    private void ShowPage(bool showA)
    {
        if (KeepAttachedSwitch.IsChecked == true)
        {
            PageA.IsVisible = showA;
            PageB.IsVisible = !showA;
            Log($"page {(showA ? "A" : "B")} via IsVisible; host stays in the visual tree");
            return;
        }

        PageA.IsVisible = true;
        PageB.IsVisible = false;
        SessionSlot.Content = showA ? _host : null;
        Log($"page {(showA ? "A" : "B")} by taking the host out of the visual tree");
    }

    private void Sink_OnRaised(object? sender, RdpEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var detail = e.Name == "OnDisconnected" && e.Arguments.Count > 0 && _session is not null
                ? $"{e} — {_session.DescribeDisconnect(Convert.ToUInt32(e.Arguments[0], CultureInfo.InvariantCulture))}"
                : e.ToString();
            _events.Add(detail);
            _state = e.Name switch
            {
                "OnConnecting" => "connecting",
                "OnConnected" => "connected",
                "OnLoginComplete" => "logged in",
                "OnDisconnected" => "disconnected",
                _ => _state,
            };
            Log($"event: {detail}");
        });
    }

    private (int Width, int Height) TargetPixelSize()
    {
        var bounds = SessionSlot.Bounds;
        var width = bounds.Width > 16 ? bounds.Width : _launchOptions.Width;
        var height = bounds.Height > 16 ? bounds.Height : _launchOptions.Height;
        return ((int)Math.Round(width * RenderScaling), (int)Math.Round(height * RenderScaling));
    }

    private (uint Desktop, uint Device) ScaleFactors()
    {
        // DesktopScaleFactor is the remote session's scaling in percent and the protocol only accepts
        // 100/140/180. DeviceScaleFactor is the client device's, and only accepts 100/140/180 as well.
        var percent = (uint)Math.Round(RenderScaling * 100);
        var desktop = percent switch
        {
            >= 180 => 180u,
            >= 140 => 140u,
            _ => 100u,
        };

        return (desktop, desktop);
    }

    private void UpdateStatus()
    {
        if (Math.Abs(RenderScaling - _lastScaling) > 0.001)
        {
            Log($"RenderScaling changed {_lastScaling:F2} -> {RenderScaling:F2} " +
                $"(window DPI {Win32.GetDpiForWindow(WindowHandle())})");
            _lastScaling = RenderScaling;
        }

        var (width, height) = TargetPixelSize();
        StatusText.Text =
            $"{_state} | class={_activeClass?.Candidate.CoClassName ?? "none"} | " +
            $"container={_container?.Name ?? "none"} | {_session?.ConnectionState ?? "no session"} | " +
            $"target={width}x{height} px | scaling={RenderScaling:F2}";

        var ole = _container as OleSiteContainer;
        MetricsText.Text =
            $"attach/detach = {_host?.AttachCount ?? 0}/{_host?.DetachCount ?? 0} | " +
            $"{_keyboard?.Summary ?? "keyboard probe off"} | " +
            $"site TranslateAccelerator calls = {(ole is null ? "n/a" : ole.TranslateAcceleratorCalls.ToString(CultureInfo.InvariantCulture))} | " +
            $"site OnFocus calls = {(ole is null ? "n/a" : ole.OnFocusCalls.ToString(CultureInfo.InvariantCulture))}";
    }

    private IntPtr WindowHandle()
    {
        return TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
    }

    private async void ExportEvidence_OnClick(object? sender, RoutedEventArgs e)
    {
        var evidence = new
        {
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Os = Environment.OSVersion.VersionString,
            ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            OsArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            Classes = _probes.Select(probe => probe.Summary),
            ActiveClass = _activeClass?.Candidate.CoClassName,
            Container = _container?.Name,
            ContainerNotes = _container?.Notes,
            State = _state,
            Attach = _host?.AttachCount ?? 0,
            Detach = _host?.DetachCount ?? 0,
            Keyboard = new
            {
                InsideControl = _keyboard?.InsideControlCount ?? 0,
                Elsewhere = _keyboard?.ElsewhereCount ?? 0,
                Recent = _keyboard?.Recent().Select(key =>
                    $"{key.Message} vk=0x{key.VirtualKey:X2} hwnd=0x{key.Target:X} inside={key.InsideControl}"),
            },
            RenderScaling,
            Events = _events,
        };

        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "rdp-spike");
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"evidence-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, _jsonSerializerOptions));
        Log($"evidence written to {path}");
    }

    private void TearDown()
    {
        _statusTimer.Stop();
        Destroy_OnClick(null, new RoutedEventArgs());
        _keyboard?.Dispose();
        _keyboard = null;
    }

    /// <summary>Everything the spike learns goes to the window and to a file at the same time. The file is
    /// what makes a run reviewable afterwards, and what the ADR quotes.</summary>
    private void Log(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}";
        _ = _log.AppendLine(line);
        LogTextBox.Text = _log.ToString();
        LogTextBox.CaretIndex = LogTextBox.Text.Length;

        try
        {
            File.AppendAllText(_logPath.Value, line + Environment.NewLine);
        }
        catch (IOException)
        {
            // A run that cannot write its log is still a run worth watching in the window.
        }
    }

    private static (string Host, int Port) SplitHost(string value)
    {
        var trimmed = value.Trim();
        var separator = trimmed.LastIndexOf(':');
        return separator > 0 &&
            int.TryParse(trimmed[(separator + 1)..], CultureInfo.InvariantCulture, out var port)
            ? (trimmed[..separator], port)
            : (trimmed, 3389);
    }
}
