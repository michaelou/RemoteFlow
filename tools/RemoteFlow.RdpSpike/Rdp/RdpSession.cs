using System.Globalization;
using System.Runtime.InteropServices;
using RemoteFlow.RdpSpike.Interop;

namespace RemoteFlow.RdpSpike.Rdp;

/// <summary>A thin typed facade over the activated control. Every call reports its HRESULT rather than
/// throwing, because what the spike is measuring is which calls the control accepts and when.</summary>
internal sealed class RdpSession : IDisposable
{
    private readonly IMsRdpClient10 _client;
    private readonly IMsRdpClientAdvancedSettings8? _advanced;
    private readonly IMsRdpClientNonScriptable5? _nonScriptable;
    private readonly IMsRdpExtendedSettings? _extended;
    private readonly Action<string> _log;

    private RdpSession(
        IMsRdpClient10 client,
        IMsRdpClientAdvancedSettings8? advanced,
        IMsRdpClientNonScriptable5? nonScriptable,
        IMsRdpExtendedSettings? extended,
        Action<string> log)
    {
        _client = client;
        _advanced = advanced;
        _nonScriptable = nonScriptable;
        _extended = extended;
        _log = log;
    }

    public bool HasAdvancedSettings => _advanced is not null;

    public bool HasExtendedSettings => _extended is not null;

    public bool HasNonScriptable => _nonScriptable is not null;

    /// <summary>Wraps an activated control, or returns null when it does not offer IMsRdpClient10.</summary>
    public static RdpSession? Create(object control, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(log);
        if (control is not IMsRdpClient10 client)
        {
            log("QueryInterface(IMsRdpClient10) failed: this class predates the interface.");
            return null;
        }

        IMsRdpClientAdvancedSettings8? advanced = null;
        var hr = client.get_AdvancedSettings9(out var advancedPointer);
        if (hr == Ole.S_OK && advancedPointer != IntPtr.Zero)
        {
            advanced = (IMsRdpClientAdvancedSettings8)Marshal.GetObjectForIUnknown(advancedPointer);
            _ = Marshal.Release(advancedPointer);
        }
        else
        {
            log($"get_AdvancedSettings9 -> 0x{hr:X8}");
        }

        return new RdpSession(
            client,
            advanced,
            control as IMsRdpClientNonScriptable5,
            control as IMsRdpExtendedSettings,
            log);
    }

    public string? Version => Report("get_Version", _client.get_Version(out var version)) ? version : null;

    /// <summary>The control's own three-state answer. It matters that 2 is "still connecting": treating
    /// anything non-zero as connected would let the spike claim a session it has not got.</summary>
    public string ConnectionState =>
        _client.get_Connected(out var connected) != Ole.S_OK
            ? "unavailable"
            : connected switch
            {
                0 => "disconnected",
                1 => "connected",
                2 => "connecting",
                _ => $"unknown ({connected})",
            };

    public bool IsConnected => _client.get_Connected(out var connected) == Ole.S_OK && connected == 1;

    /// <summary>Sets everything that has to be in place before Connect. The RDP control rejects most of
    /// these once the session is up, which is why the spike sets them in one place.</summary>
    public void Configure(SpikeOptions options, int width, int height, uint desktopScaleFactor, uint deviceScaleFactor)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = Report("put_Server", _client.put_Server(options.Host));
        _ = Report("put_DesktopWidth", _client.put_DesktopWidth(width));
        _ = Report("put_DesktopHeight", _client.put_DesktopHeight(height));
        _ = Report("put_ColorDepth", _client.put_ColorDepth(32));

        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            _ = Report("put_UserName", _client.put_UserName(options.UserName));
        }

        if (!string.IsNullOrWhiteSpace(options.Domain))
        {
            _ = Report("put_Domain", _client.put_Domain(options.Domain));
        }

        if (_advanced is not null)
        {
            _ = Report("put_RDPPort", _advanced.put_RDPPort(options.Port));
            _ = Report("put_SmartSizing", _advanced.put_SmartSizing(options.SmartSizing ? (short)-1 : (short)0));
            _ = Report("put_EnableAutoReconnect", _advanced.put_EnableAutoReconnect(0));
            _ = Report("put_AuthenticationLevel", _advanced.put_AuthenticationLevel(2));
            _ = Report(
                "put_EnableCredSspSupport",
                _advanced.put_EnableCredSspSupport(options.CredSsp ? (short)-1 : (short)0));

            // The connection bar is a full-screen affordance. Embedded sessions never go full screen
            // through the control, so it only ever gets in the way.
            _ = Report("put_ContainerHandledFullScreen", _advanced.put_ContainerHandledFullScreen(1));
            _ = Report("put_GrabFocusOnConnect", _advanced.put_GrabFocusOnConnect(0));
        }

        if (_nonScriptable is not null)
        {
            _ = Report(
                "put_AllowPromptingForCredentials",
                _nonScriptable.put_AllowPromptingForCredentials(options.PromptForCredentials ? (short)-1 : (short)0));
        }

        // DesktopScaleFactor and DeviceScaleFactor have no property of their own on any IMsRdpClient
        // interface. Before a connection they are only reachable through the extended-settings property
        // bag; afterwards, only through UpdateSessionDisplaySettings.
        if (_extended is not null && desktopScaleFactor > 0)
        {
            SetScaleFactor("DesktopScaleFactor", desktopScaleFactor);
            SetScaleFactor("DeviceScaleFactor", deviceScaleFactor);
        }
        else
        {
            _log("no IMsRdpExtendedSettings: scale factors can only be set with UpdateSessionDisplaySettings");
        }
    }

    public bool Connect()
    {
        return Report("Connect", _client.Connect());
    }

    public bool Disconnect()
    {
        return Report("Disconnect", _client.Disconnect());
    }

    /// <summary>Resizes the remote desktop on a live session without dropping it.</summary>
    public bool Reconnect(int width, int height)
    {
        var hr = _client.Reconnect((uint)Math.Max(width, 1), (uint)Math.Max(height, 1), out var status);
        _log($"Reconnect({width}x{height}) -> 0x{hr:X8}, status={DescribeReconnectStatus(status)}");
        return hr == Ole.S_OK && status == 0;
    }

    /// <summary>The other resize path: the one that also carries DPI.</summary>
    public bool UpdateSessionDisplaySettings(
        int width,
        int height,
        uint desktopScaleFactor,
        uint deviceScaleFactor)
    {
        var hr = _client.UpdateSessionDisplaySettings(
            (uint)Math.Max(width, 1),
            (uint)Math.Max(height, 1),
            0,
            0,
            0,
            desktopScaleFactor,
            deviceScaleFactor);
        _log(
            $"UpdateSessionDisplaySettings({width}x{height}, desktopScale={desktopScaleFactor}, " +
            $"deviceScale={deviceScaleFactor}) -> 0x{hr:X8}");
        return hr == Ole.S_OK;
    }

    public string DescribeDisconnect(uint reason)
    {
        _ = _client.get_ExtendedDisconnectReason(out var extended);
        return _client.GetErrorDescription(reason, (uint)extended, out var description) == Ole.S_OK
            ? $"{description} (reason {reason}, extended {extended})"
            : $"reason {reason}, extended {extended}";
    }

    /// <summary>Releases only what this class created.
    ///
    /// IMsRdpClientNonScriptable5 and IMsRdpExtendedSettings are QueryInterface casts of the control's own
    /// RCW, not separate objects: releasing either one separates the RCW the container is still holding,
    /// and the container's own teardown then fails with InvalidComObjectException. Only the settings object
    /// handed out by get_AdvancedSettings9 is a distinct COM identity, and it is released with the control
    /// anyway, so this disposes nothing at all — the comment is the point.</summary>
    public void Dispose()
    {
    }

    private static string DescribeReconnectStatus(int status)
    {
        return status switch
        {
            0 => "ControlReconnectStarted",
            1 => "ControlReconnectBlocked",
            2 => "ControlReconnectNoSession",
            _ => status.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Sets one scale factor through the extended-settings property bag and reads it straight
    /// back. A property bag that accepts a write it then does not remember would answer question 6 the
    /// wrong way round, so the read-back is the evidence, not the write.</summary>
    private void SetScaleFactor(string name, uint value)
    {
        if (_extended is null)
        {
            return;
        }

        var written = _extended.put_Property(name, value);
        var read = _extended.get_Property(name, out var stored);
        _log(
            $"IMsRdpExtendedSettings[{name}] = {value}: put -> 0x{written:X8}, " +
            $"get -> 0x{read:X8} value={stored ?? "(null)"}");
    }

    private bool Report(string call, int hr)
    {
        if (hr != Ole.S_OK)
        {
            _log($"{call} -> 0x{hr:X8}");
        }

        return hr == Ole.S_OK;
    }
}
