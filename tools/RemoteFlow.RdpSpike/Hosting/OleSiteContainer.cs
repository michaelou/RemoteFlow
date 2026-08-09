using System.Runtime.InteropServices;
using RemoteFlow.RdpSpike.Interop;

namespace RemoteFlow.RdpSpike.Hosting;

/// <summary>A hand-rolled OLE container: a plain Win32 window plus the site interfaces an ActiveX control
/// asks for during in-place activation. No WinForms, so nothing here depends on a message loop that
/// Avalonia does not run.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class OleSiteContainer : IActiveXContainer, IOleClientSite, IOleInPlaceSite, IOleInPlaceFrame,
    IOleControlSite, IDispatchSite
{
    private const string _windowClassName = "RemoteFlowRdpSpikeOleSite";

    private static ushort _registeredClass;
    private static Win32.WndProc? _windowProcedure;

    private readonly List<string> _notes = [];

    private IntPtr _controlUnknown;
    private IOleObject? _oleObject;
    private IOleInPlaceObject? _inPlaceObject;
    private int _width;
    private int _height;

    public string Name => "hand-rolled IOleClientSite/IOleInPlaceSite";

    public IntPtr Handle { get; private set; }

    public object? Control { get; private set; }

    public IReadOnlyList<string> Notes => _notes;

    /// <summary>How many times the control asked the site to translate an accelerator. Zero after a
    /// keyboard test means the control takes keystrokes straight off its own window and the container's
    /// message loop never gets a say — which is the answer question 5 needs.</summary>
    public int TranslateAcceleratorCalls { get; private set; }

    public int OnFocusCalls { get; private set; }

    public void Create(Guid classId, int width, int height)
    {
        _width = Math.Max(width, 1);
        _height = Math.Max(height, 1);
        EnsureWindowClass();

        // The host window starts parentless. Avalonia's NativeControlHost reparents it on attach, and
        // the offscreen holder takes it back on detach; the control never notices either.
        Handle = Win32.CreateWindowEx(
            0,
            _windowClassName,
            null,
            Win32.WS_POPUP | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS,
            0,
            0,
            _width,
            _height,
            IntPtr.Zero,
            IntPtr.Zero,
            Win32.GetModuleHandle(null),
            IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowEx failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }

        var unknown = new Guid("00000000-0000-0000-C000-000000000046");
        var hr = Win32.CoCreateInstance(
            classId,
            IntPtr.Zero,
            Win32.CLSCTX_INPROC_SERVER,
            unknown,
            out _controlUnknown);
        if (hr != Ole.S_OK)
        {
            throw new InvalidOperationException($"CoCreateInstance({classId:B}) failed: 0x{hr:X8}");
        }

        Control = Marshal.GetObjectForIUnknown(_controlUnknown);
        _oleObject = Control as IOleObject
            ?? throw new InvalidOperationException("The control does not implement IOleObject.");

        Note($"QueryInterface: IOleObject=yes, IOleControl={(Control is IOleControl ? "yes" : "no")}, " +
            $"IOleInPlaceObject={(Control is IOleInPlaceObject ? "yes" : "no")}, " +
            $"IOleInPlaceActiveObject={(Control is IOleInPlaceActiveObject ? "yes" : "no")}");

        Note($"IOleObject.SetClientSite -> 0x{_oleObject.SetClientSite(this):X8}");
        Note($"IOleObject.SetHostNames -> 0x{_oleObject.SetHostNames("RemoteFlow.RdpSpike", null):X8}");

        if (_oleObject.GetMiscStatus(1 /* DVASPECT_CONTENT */, out var miscStatus) == Ole.S_OK)
        {
            Note($"IOleObject.GetMiscStatus -> 0x{miscStatus:X8}");
        }

        var bounds = new Rect(0, 0, _width, _height);
        var verb = _oleObject.DoVerb(Ole.OLEIVERB_INPLACEACTIVATE, IntPtr.Zero, this, 0, Handle, bounds);
        Note($"IOleObject.DoVerb(OLEIVERB_INPLACEACTIVATE) -> 0x{verb:X8}");
        if (verb != Ole.S_OK)
        {
            throw new InvalidOperationException($"In-place activation failed: 0x{verb:X8}");
        }

        _inPlaceObject = Control as IOleInPlaceObject;
        if (_inPlaceObject is not null && _inPlaceObject.GetWindow(out var controlWindow) == Ole.S_OK)
        {
            Note($"control HWND = 0x{controlWindow:X}, parented under host = " +
                $"{Win32.IsChild(Handle, controlWindow)}");
        }

        SetSize(_width, _height);
    }

    public void SetSize(int width, int height)
    {
        _width = Math.Max(width, 1);
        _height = Math.Max(height, 1);
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        _ = Win32.MoveWindow(Handle, 0, 0, _width, _height, repaint: true);
        var bounds = new Rect(0, 0, _width, _height);
        _ = _inPlaceObject?.SetObjectRects(bounds, bounds);
    }

    public bool FocusControl()
    {
        if (_inPlaceObject is null || _inPlaceObject.GetWindow(out var controlWindow) != Ole.S_OK)
        {
            return false;
        }

        _ = Win32.SetFocus(controlWindow);
        var focused = Win32.GetFocus();
        return focused == controlWindow || Win32.IsChild(controlWindow, focused);
    }

    public void Dispose()
    {
        if (_oleObject is not null)
        {
            _ = _inPlaceObject?.InPlaceDeactivate();
            _ = _oleObject.Close(Ole.OLECLOSE_NOSAVE);
            _ = _oleObject.SetClientSite(null);
        }

        _inPlaceObject = null;
        _oleObject = null;

        if (Control is not null)
        {
            _ = Marshal.ReleaseComObject(Control);
            Control = null;
        }

        if (_controlUnknown != IntPtr.Zero)
        {
            _ = Marshal.Release(_controlUnknown);
            _controlUnknown = IntPtr.Zero;
        }

        if (Handle != IntPtr.Zero)
        {
            _ = Win32.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }

    private void Note(string note)
    {
        _notes.Add(note);
    }

    private static void EnsureWindowClass()
    {
        if (_registeredClass != 0)
        {
            return;
        }

        // Held in a static so the delegate outlives the window class registration.
        _windowProcedure = Win32.DefWindowProc;
        var windowClass = new Win32.WndClassEx
        {
            CbSize = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
            LpfnWndProc = _windowProcedure,
            HInstance = Win32.GetModuleHandle(null),
            LpszClassName = _windowClassName,
        };

        _registeredClass = Win32.RegisterClassEx(windowClass);
        if (_registeredClass == 0)
        {
            throw new InvalidOperationException(
                $"RegisterClassEx failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }
    }

    // IOleClientSite ---------------------------------------------------------------------------------

    int IOleClientSite.SaveObject()
    {
        return Ole.E_NOTIMPL;
    }

    int IOleClientSite.GetMoniker(uint dwAssign, uint dwWhichMoniker, out IntPtr ppmk)
    {
        ppmk = IntPtr.Zero;
        return Ole.E_NOTIMPL;
    }

    int IOleClientSite.GetContainer(out IntPtr ppContainer)
    {
        // No IOleContainer: the control must not enumerate its siblings, because it has none.
        ppContainer = IntPtr.Zero;
        return Ole.E_NOINTERFACE;
    }

    int IOleClientSite.ShowObject()
    {
        return Ole.S_OK;
    }

    int IOleClientSite.OnShowWindow(int fShow)
    {
        return Ole.S_OK;
    }

    int IOleClientSite.RequestNewObjectLayout()
    {
        return Ole.E_NOTIMPL;
    }

    // IOleInPlaceSite -------------------------------------------------------------------------------

    int IOleInPlaceSite.GetWindow(out IntPtr phwnd)
    {
        phwnd = Handle;
        return Ole.S_OK;
    }

    int IOleInPlaceSite.ContextSensitiveHelp(int fEnterMode)
    {
        return Ole.E_NOTIMPL;
    }

    int IOleInPlaceSite.CanInPlaceActivate()
    {
        return Ole.S_OK;
    }

    int IOleInPlaceSite.OnInPlaceActivate()
    {
        Note("site: OnInPlaceActivate");
        return Ole.S_OK;
    }

    int IOleInPlaceSite.OnUIActivate()
    {
        Note("site: OnUIActivate");
        return Ole.S_OK;
    }

    int IOleInPlaceSite.GetWindowContext(
        out IntPtr ppFrame,
        out IntPtr ppDoc,
        out Rect lprcPosRect,
        out Rect lprcClipRect,
        ref OleInPlaceFrameInfo lpFrameInfo)
    {
        ppFrame = Marshal.GetComInterfaceForObject<OleSiteContainer, IOleInPlaceFrame>(this);
        ppDoc = IntPtr.Zero;
        lprcPosRect = new Rect(0, 0, _width, _height);
        lprcClipRect = lprcPosRect;
        lpFrameInfo.Cb = (uint)Marshal.SizeOf<OleInPlaceFrameInfo>();
        lpFrameInfo.FMdiApp = 0;
        lpFrameInfo.HwndFrame = Handle;
        lpFrameInfo.Haccel = IntPtr.Zero;
        lpFrameInfo.CAccelEntries = 0;
        return Ole.S_OK;
    }

    int IOleInPlaceSite.Scroll(SizeL scrollExtant)
    {
        return Ole.E_NOTIMPL;
    }

    int IOleInPlaceSite.OnUIDeactivate(int fUndoable)
    {
        return Ole.S_OK;
    }

    int IOleInPlaceSite.OnInPlaceDeactivate()
    {
        return Ole.S_OK;
    }

    int IOleInPlaceSite.DiscardUndoState()
    {
        return Ole.E_NOTIMPL;
    }

    int IOleInPlaceSite.DeactivateAndUndo()
    {
        return Ole.E_NOTIMPL;
    }

    int IOleInPlaceSite.OnPosRectChange(in Rect lprcPosRect)
    {
        _ = _inPlaceObject?.SetObjectRects(lprcPosRect, lprcPosRect);
        return Ole.S_OK;
    }

    // IOleInPlaceFrame ------------------------------------------------------------------------------

    int IOleInPlaceFrame.GetWindow(out IntPtr phwnd)
    {
        phwnd = Handle;
        return Ole.S_OK;
    }

    int IOleInPlaceFrame.ContextSensitiveHelp(int fEnterMode)
    {
        return Ole.E_NOTIMPL;
    }

    int IOleInPlaceFrame.GetBorder(out Rect lprectBorder)
    {
        lprectBorder = new Rect(0, 0, _width, _height);
        return Ole.S_OK;
    }

    int IOleInPlaceFrame.RequestBorderSpace(in Rect pborderwidths)
    {
        return Ole.S_OK;
    }

    int IOleInPlaceFrame.SetBorderSpace(in Rect pborderwidths)
    {
        return Ole.S_OK;
    }

    int IOleInPlaceFrame.SetActiveObject(IntPtr pActiveObject, string? pszObjName)
    {
        return Ole.S_OK;
    }

    int IOleInPlaceFrame.InsertMenus(IntPtr hmenuShared, IntPtr lpMenuWidths)
    {
        return Ole.E_NOTIMPL;
    }

    int IOleInPlaceFrame.SetMenu(IntPtr hmenuShared, IntPtr holemenu, IntPtr hwndActiveObject)
    {
        return Ole.S_OK;
    }

    int IOleInPlaceFrame.RemoveMenus(IntPtr hmenuShared)
    {
        return Ole.E_NOTIMPL;
    }

    int IOleInPlaceFrame.SetStatusText(string? pszStatusText)
    {
        return Ole.S_OK;
    }

    int IOleInPlaceFrame.EnableModeless(int fEnable)
    {
        return Ole.S_OK;
    }

    int IOleInPlaceFrame.TranslateAccelerator(in Msg lpmsg, ushort wID)
    {
        TranslateAcceleratorCalls++;
        return Ole.S_FALSE;
    }

    // IOleControlSite -------------------------------------------------------------------------------

    int IOleControlSite.OnControlInfoChanged()
    {
        return Ole.S_OK;
    }

    int IOleControlSite.LockInPlaceActive(int fLock)
    {
        return Ole.S_OK;
    }

    int IOleControlSite.GetExtendedControl(out IntPtr ppDisp)
    {
        ppDisp = IntPtr.Zero;
        return Ole.E_NOTIMPL;
    }

    int IOleControlSite.TransformCoords(ref PointL pPtlHimetric, ref PointF32 pPtfContainer, uint dwFlags)
    {
        return Ole.E_NOTIMPL;
    }

    int IOleControlSite.TranslateAccelerator(in Msg pMsg, uint grfModifiers)
    {
        TranslateAcceleratorCalls++;
        return Ole.S_FALSE;
    }

    int IOleControlSite.OnFocus(int fGotFocus)
    {
        OnFocusCalls++;
        return Ole.S_OK;
    }

    int IOleControlSite.ShowPropertyFrame()
    {
        return Ole.E_NOTIMPL;
    }

    // IDispatch, for ambient properties --------------------------------------------------------------

    int IDispatchSite.GetTypeInfoCount(out uint pctinfo)
    {
        pctinfo = 0;
        return Ole.S_OK;
    }

    int IDispatchSite.GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo)
    {
        pptinfo = IntPtr.Zero;
        return Ole.E_NOTIMPL;
    }

    int IDispatchSite.GetIDsOfNames(in Guid riid, IntPtr rgszNames, uint cNames, uint lcid, IntPtr rgdispid)
    {
        return Ole.E_NOTIMPL;
    }

    int IDispatchSite.Invoke(
        int dispid,
        in Guid riid,
        uint lcid,
        ushort flags,
        IntPtr pDispParams,
        IntPtr pVarResult,
        IntPtr pExcepInfo,
        IntPtr puArgErr)
    {
        // Every ambient property is left at the control's own default. Saying "not found" is the
        // documented way to do that, and the RDP control has sensible defaults for all of them.
        return Ole.DISP_E_MEMBERNOTFOUND;
    }
}
