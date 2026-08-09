using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using RemoteFlow.RdpSpike.Interop;

namespace RemoteFlow.RdpSpike.Rdp;

internal sealed record RdpEvent(string Name, IReadOnlyList<object?> Arguments)
{
    public override string ToString()
    {
        return Arguments.Count == 0
            ? Name
            : $"{Name}({string.Join(", ", Arguments.Select(argument => argument?.ToString() ?? "null"))})";
    }
}

/// <summary>Receives IMsTscAxEvents through a connection point. The sink implements the dispinterface as
/// a raw IDispatch and switches on the dispid, which is all a dispinterface source ever needs: there is no
/// type library to import and no generated wrapper in the picture.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class RdpEventSink : IMsTscAxEventsSink, IDisposable
{
    /// <summary>Dispids read out of the MSTSCLib type library. The names are the payload of the spike's
    /// log, so a dispid with no entry here is reported by number rather than swallowed.</summary>
    private static readonly Dictionary<int, string> _names = new()
    {
        [1] = "OnConnecting",
        [2] = "OnConnected",
        [3] = "OnLoginComplete",
        [4] = "OnDisconnected",
        [5] = "OnEnterFullScreenMode",
        [6] = "OnLeaveFullScreenMode",
        [7] = "OnChannelReceivedData",
        [8] = "OnRequestGoFullScreen",
        [9] = "OnRequestLeaveFullScreen",
        [10] = "OnFatalError",
        [11] = "OnWarning",
        [12] = "OnRemoteDesktopSizeChange",
        [13] = "OnIdleTimeoutNotification",
        [14] = "OnRequestContainerMinimize",
        [15] = "OnConfirmClose",
        [16] = "OnReceivedTSPublicKey",
        [17] = "OnAutoReconnecting",
        [18] = "OnAuthenticationWarningDisplayed",
        [19] = "OnAuthenticationWarningDismissed",
        [20] = "OnRemoteProgramResult",
        [21] = "OnRemoteProgramDisplayed",
        [22] = "OnLogonError",
        [23] = "OnFocusReleased",
        [24] = "OnUserNameAcquired",
        [26] = "OnMouseInputModeChanged",
        [28] = "OnServiceMessageReceived",
        [29] = "OnRemoteWindowDisplayed",
        [30] = "OnConnectionBarPullDown",
        [32] = "OnNetworkStatusChanged",
        [33] = "OnAutoReconnected",
        [34] = "OnAutoReconnecting2",
        [35] = "OnDevicesButtonPressed",
        [41] = "OnLogonEvent",
    };

    private IConnectionPoint? _connectionPoint;
    private int _cookie;

    public event EventHandler<RdpEvent>? Raised;

    public bool IsAdvised => _connectionPoint is not null;

    /// <summary>Advises this sink on the control's IMsTscAxEvents connection point.</summary>
    /// <returns>An empty string on success, or the reason it did not happen.</returns>
    public string Advise(object control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (control is not IConnectionPointContainer container)
        {
            return "the control does not implement IConnectionPointContainer";
        }

        var iid = typeof(IMsTscAxEventsSink).GUID;
        try
        {
            container.FindConnectionPoint(ref iid, out var point);
            if (point is null)
            {
                return "the control has no IMsTscAxEvents connection point";
            }

            point.Advise(this, out _cookie);
            _connectionPoint = point;
            return string.Empty;
        }
        catch (COMException exception)
        {
            return $"FindConnectionPoint/Advise failed: 0x{exception.HResult:X8} {exception.Message}";
        }
    }

    public int GetTypeInfoCount(out uint pctinfo)
    {
        pctinfo = 0;
        return Ole.S_OK;
    }

    public int GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo)
    {
        pptinfo = IntPtr.Zero;
        return Ole.E_NOTIMPL;
    }

    public int GetIDsOfNames(in Guid riid, IntPtr rgszNames, uint cNames, uint lcid, IntPtr rgdispid)
    {
        return Ole.E_NOTIMPL;
    }

    public int Invoke(
        int dispid,
        in Guid riid,
        uint lcid,
        ushort flags,
        IntPtr pDispParams,
        IntPtr pVarResult,
        IntPtr pExcepInfo,
        IntPtr puArgErr)
    {
        var name = _names.TryGetValue(dispid, out var known) ? known : $"dispid {dispid}";
        Raised?.Invoke(this, new RdpEvent(name, ReadArguments(pDispParams)));
        return Ole.S_OK;
    }

    public void Dispose()
    {
        if (_connectionPoint is null)
        {
            return;
        }

        try
        {
            _connectionPoint.Unadvise(_cookie);
        }
        catch (COMException)
        {
            // The control has already torn the connection point down. Nothing left to unadvise.
        }

        _ = Marshal.ReleaseComObject(_connectionPoint);
        _connectionPoint = null;
    }

    /// <summary>DISPPARAMS holds its arguments in reverse order, so they are reversed back here to read
    /// the way the type library declares them.</summary>
    private static List<object?> ReadArguments(IntPtr pDispParams)
    {
        if (pDispParams == IntPtr.Zero)
        {
            return [];
        }

        var parameters = Marshal.PtrToStructure<DispParams>(pDispParams);
        if (parameters.CArgs == 0 || parameters.Rgvarg == IntPtr.Zero)
        {
            return [];
        }

        // sizeof(VARIANT): 8 bytes of header plus the widest union member, which is 8 bytes on 32-bit
        // and the 16-byte BRECORD pair on 64-bit.
        var variantSize = IntPtr.Size == 8 ? 24 : 16;
        var arguments = new List<object?>((int)parameters.CArgs);
        for (var index = (int)parameters.CArgs - 1; index >= 0; index--)
        {
            try
            {
                arguments.Add(Marshal.GetObjectForNativeVariant(parameters.Rgvarg + (index * variantSize)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                arguments.Add("<unreadable variant>");
            }
        }

        return arguments;
    }
}
