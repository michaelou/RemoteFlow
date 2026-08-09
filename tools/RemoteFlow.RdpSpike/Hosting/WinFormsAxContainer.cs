using System.Globalization;
using RemoteFlow.RdpSpike.Interop;

namespace RemoteFlow.RdpSpike.Hosting;

/// <summary>The other candidate container: WinForms' own ActiveX host. `AxHost` is public and abstract, so
/// the control can be hosted without an `aximp`-generated wrapper — subclass it, pass the CLSID, and take
/// the object back out of <see cref="AxHost.GetOcx"/>.
///
/// The risk this container exists to measure is the message loop. Avalonia runs its own Win32 pump and
/// never calls <c>Application.Run</c>, so WinForms' <c>PreProcessMessage</c> path — the one that feeds
/// dialog keys and accelerators to hosted controls — is never reached.</summary>
internal sealed class WinFormsAxContainer : IActiveXContainer
{
    private readonly List<string> _notes = [];

    private Panel? _panel;
    private RdpAxHost? _host;

    public string Name => "WinForms AxHost";

    public IntPtr Handle => _panel?.Handle ?? IntPtr.Zero;

    public object? Control => _host?.Ocx;

    public IReadOnlyList<string> Notes => _notes;

    public void Create(Guid classId, int width, int height)
    {
        _panel = new Panel
        {
            Width = Math.Max(width, 1),
            Height = Math.Max(height, 1),
            Dock = DockStyle.None,
        };

        // Forcing the handle here means the panel is a real HWND before Avalonia reparents it, so the
        // ActiveX control below never has its window recreated by WinForms' own lazy handle creation.
        _ = _panel.Handle;

        _host = new RdpAxHost(classId.ToString()) { Dock = DockStyle.Fill };
        _panel.Controls.Add(_host);
        _ = _host.Handle;

        _notes.Add($"AxHost.CreateControl -> handle 0x{_host.Handle:X}");
        _notes.Add($"AxHost.GetOcx -> {(_host.Ocx is null ? "null" : _host.Ocx.GetType().Name)}");
        _notes.Add(
            "WinForms Application.Run is never entered under Avalonia, so Control.PreProcessMessage and " +
            "Application.AddMessageFilter are dead code in this container.");
        _notes.Add($"Application.MessageLoop = {Application.MessageLoop.ToString(CultureInfo.InvariantCulture)}");
    }

    public void SetSize(int width, int height)
    {
        if (_panel is null)
        {
            return;
        }

        _panel.Size = new System.Drawing.Size(Math.Max(width, 1), Math.Max(height, 1));
    }

    public bool FocusControl()
    {
        if (_host is null)
        {
            return false;
        }

        _ = _host.Focus();
        var focused = Win32.GetFocus();
        return focused == _host.Handle || Win32.IsChild(_host.Handle, focused);
    }

    public void Dispose()
    {
        _host?.Dispose();
        _host = null;
        _panel?.Dispose();
        _panel = null;
    }

    /// <summary>An AxHost bound to a CLSID at runtime. AttachInterfaces is where a generated wrapper would
    /// cast to its typed interface; the spike keeps the raw object and does its own QueryInterface.</summary>
    private sealed class RdpAxHost(string classId) : AxHost(classId)
    {
        public object? Ocx { get; private set; }

        protected override void AttachInterfaces()
        {
            Ocx = GetOcx();
        }
    }
}
