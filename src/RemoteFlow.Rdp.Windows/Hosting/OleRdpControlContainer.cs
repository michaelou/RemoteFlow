using System.Runtime.InteropServices;
using RemoteFlow.Rdp.Windows.Interop;

#pragma warning disable IDE0022 // The OLE site is an HRESULT dispatch table; expression bodies keep stubs auditable.

namespace RemoteFlow.Rdp.Windows.Hosting;

/// <summary>A plain Win32 window and hand-rolled OLE site for an already-created RDP ActiveX control.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class OleRdpControlContainer : IRdpControlContainer, IOleClientSite, IOleInPlaceSite,
    IOleInPlaceFrame, IOleControlSite, IDispatchSite
{
    private const string _windowClassName = "RemoteFlowRdpOleSite";
    private static ushort _registeredClass;
    private static Win32Hosting.WindowProcedure? _windowProcedure;
    private readonly object _control;
    private IOleObject? _oleObject;
    private IOleInPlaceObject? _inPlaceObject;
    private int _width;
    private int _height;
    private int _disposed;

    public OleRdpControlContainer(INativeRdpControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        _control = control.NativeInstance;
    }

    public IntPtr Handle { get; private set; }

    public event EventHandler<RdpControlFocusChangedEventArgs>? FocusChanged;

    public void Create(int width, int height)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Handle != IntPtr.Zero)
        {
            return;
        }

        _width = Math.Max(width, 1);
        _height = Math.Max(height, 1);
        EnsureWindowClass();
        Handle = Win32Hosting.CreateWindowEx(
            0,
            _windowClassName,
            null,
            Win32Hosting.WsPopup | Win32Hosting.WsClipChildren | Win32Hosting.WsClipSiblings,
            0,
            0,
            _width,
            _height,
            IntPtr.Zero,
            IntPtr.Zero,
            Win32Hosting.GetModuleHandle(null),
            IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }

        try
        {
            _oleObject = _control as IOleObject
                ?? throw new InvalidOperationException("The RDP control does not implement IOleObject.");
            Marshal.ThrowExceptionForHR(_oleObject.SetClientSite(this));
            Marshal.ThrowExceptionForHR(_oleObject.SetHostNames("RemoteFlow", null));
            var bounds = new NativeRect(0, 0, _width, _height);
            Marshal.ThrowExceptionForHR(_oleObject.DoVerb(
                OleHosting.InPlaceActivateVerb,
                IntPtr.Zero,
                this,
                0,
                Handle,
                bounds));
            _inPlaceObject = _control as IOleInPlaceObject
                ?? throw new InvalidOperationException("The RDP control does not support in-place activation.");
            SetSize(_width, _height);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SetSize(int width, int height)
    {
        _width = Math.Max(width, 1);
        _height = Math.Max(height, 1);
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        _ = Win32Hosting.MoveWindow(Handle, 0, 0, _width, _height, repaint: true);
        var bounds = new NativeRect(0, 0, _width, _height);
        _ = _inPlaceObject?.SetObjectRects(bounds, bounds);
    }

    public bool FocusControl()
    {
        if (_inPlaceObject is null || _inPlaceObject.GetWindow(out var controlWindow) != OleHosting.Success)
        {
            return false;
        }

        _ = Win32Hosting.SetFocus(controlWindow);
        var focused = Win32Hosting.GetFocus();
        return focused == controlWindow || Win32Hosting.IsChild(controlWindow, focused);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_oleObject is not null)
        {
            _ = _inPlaceObject?.InPlaceDeactivate();
            _ = _oleObject.Close(OleHosting.CloseNoSave);
            _ = _oleObject.SetClientSite(null);
        }

        // Both interface fields are QueryInterface views of the session-owned control RCW. Do not call
        // ReleaseComObject on either; the session is its sole COM owner.
        _inPlaceObject = null;
        _oleObject = null;
        if (Handle != IntPtr.Zero)
        {
            _ = Win32Hosting.DestroyWindow(Handle);
            Handle = IntPtr.Zero;
        }
    }

    private static void EnsureWindowClass()
    {
        if (_registeredClass != 0)
        {
            return;
        }

        _windowProcedure = Win32Hosting.DefWindowProc;
        var windowClass = new Win32Hosting.WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<Win32Hosting.WindowClassEx>(),
            Procedure = _windowProcedure,
            Instance = Win32Hosting.GetModuleHandle(null),
            ClassName = _windowClassName,
        };
        _registeredClass = Win32Hosting.RegisterClassEx(windowClass);
        if (_registeredClass == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }
    }

    int IOleClientSite.SaveObject() => OleHosting.NotImplemented;

    int IOleClientSite.GetMoniker(uint assign, uint whichMoniker, out IntPtr moniker)
    {
        moniker = IntPtr.Zero;
        return OleHosting.NotImplemented;
    }

    int IOleClientSite.GetContainer(out IntPtr container)
    {
        container = IntPtr.Zero;
        return OleHosting.NoInterface;
    }

    int IOleClientSite.ShowObject() => OleHosting.Success;

    int IOleClientSite.OnShowWindow(int show) => OleHosting.Success;

    int IOleClientSite.RequestNewObjectLayout() => OleHosting.NotImplemented;

    int IOleInPlaceSite.GetWindow(out IntPtr window)
    {
        window = Handle;
        return OleHosting.Success;
    }

    int IOleInPlaceSite.ContextSensitiveHelp(int enterMode) => OleHosting.NotImplemented;

    int IOleInPlaceSite.CanInPlaceActivate() => OleHosting.Success;

    int IOleInPlaceSite.OnInPlaceActivate() => OleHosting.Success;

    int IOleInPlaceSite.OnUIActivate() => OleHosting.Success;

    int IOleInPlaceSite.GetWindowContext(
        out IntPtr frame,
        out IntPtr document,
        out NativeRect position,
        out NativeRect clip,
        ref OleFrameInfo frameInfo)
    {
        frame = Marshal.GetComInterfaceForObject<OleRdpControlContainer, IOleInPlaceFrame>(this);
        document = IntPtr.Zero;
        position = new NativeRect(0, 0, _width, _height);
        clip = position;
        frameInfo.Size = (uint)Marshal.SizeOf<OleFrameInfo>();
        frameInfo.IsMdiApplication = 0;
        frameInfo.FrameWindow = Handle;
        frameInfo.AcceleratorTable = IntPtr.Zero;
        frameInfo.AcceleratorCount = 0;
        return OleHosting.Success;
    }

    int IOleInPlaceSite.Scroll(NativeSize scrollExtent) => OleHosting.NotImplemented;

    int IOleInPlaceSite.OnUIDeactivate(int undoable) => OleHosting.Success;

    int IOleInPlaceSite.OnInPlaceDeactivate() => OleHosting.Success;

    int IOleInPlaceSite.DiscardUndoState() => OleHosting.NotImplemented;

    int IOleInPlaceSite.DeactivateAndUndo() => OleHosting.NotImplemented;

    int IOleInPlaceSite.OnPosRectChange(in NativeRect position)
    {
        _ = _inPlaceObject?.SetObjectRects(position, position);
        return OleHosting.Success;
    }

    int IOleInPlaceFrame.GetWindow(out IntPtr window)
    {
        window = Handle;
        return OleHosting.Success;
    }

    int IOleInPlaceFrame.ContextSensitiveHelp(int enterMode) => OleHosting.NotImplemented;

    int IOleInPlaceFrame.GetBorder(out NativeRect border)
    {
        border = new NativeRect(0, 0, _width, _height);
        return OleHosting.Success;
    }

    int IOleInPlaceFrame.RequestBorderSpace(in NativeRect borderWidths) => OleHosting.Success;

    int IOleInPlaceFrame.SetBorderSpace(in NativeRect borderWidths) => OleHosting.Success;

    int IOleInPlaceFrame.SetActiveObject(IntPtr activeObject, string? objectName) => OleHosting.Success;

    int IOleInPlaceFrame.InsertMenus(IntPtr sharedMenu, IntPtr menuWidths) => OleHosting.NotImplemented;

    int IOleInPlaceFrame.SetMenu(IntPtr sharedMenu, IntPtr oleMenu, IntPtr activeObjectWindow) => OleHosting.Success;

    int IOleInPlaceFrame.RemoveMenus(IntPtr sharedMenu) => OleHosting.NotImplemented;

    int IOleInPlaceFrame.SetStatusText(string? statusText) => OleHosting.Success;

    int IOleInPlaceFrame.EnableModeless(int enable) => OleHosting.Success;

    int IOleInPlaceFrame.TranslateAccelerator(in NativeMessage message, ushort commandId) => OleHosting.False;

    int IOleControlSite.OnControlInfoChanged() => OleHosting.Success;

    int IOleControlSite.LockInPlaceActive(int locked) => OleHosting.Success;

    int IOleControlSite.GetExtendedControl(out IntPtr dispatch)
    {
        dispatch = IntPtr.Zero;
        return OleHosting.NotImplemented;
    }

    int IOleControlSite.TransformCoords(ref NativePoint himetric, ref NativeFloatPoint container, uint flags) =>
        OleHosting.NotImplemented;

    int IOleControlSite.TranslateAccelerator(in NativeMessage message, uint modifiers) => OleHosting.False;

    int IOleControlSite.OnFocus(int gotFocus)
    {
        try
        {
            FocusChanged?.Invoke(this, new RdpControlFocusChangedEventArgs(gotFocus != 0));
        }
        catch (Exception)
        {
            // Focus observers are called by COM and must not unwind into the ActiveX control.
        }
        return OleHosting.Success;
    }

    int IOleControlSite.ShowPropertyFrame() => OleHosting.NotImplemented;

    int IDispatchSite.GetTypeInfoCount(out uint count)
    {
        count = 0;
        return OleHosting.Success;
    }

    int IDispatchSite.GetTypeInfo(uint typeInfo, uint locale, out IntPtr pointer)
    {
        pointer = IntPtr.Zero;
        return OleHosting.NotImplemented;
    }

    int IDispatchSite.GetIDsOfNames(in Guid interfaceId, IntPtr names, uint nameCount, uint locale, IntPtr dispatchIds) =>
        OleHosting.NotImplemented;

    int IDispatchSite.Invoke(
        int dispatchId,
        in Guid interfaceId,
        uint locale,
        ushort flags,
        IntPtr parameters,
        IntPtr result,
        IntPtr exceptionInfo,
        IntPtr argumentError) => OleHosting.MemberNotFound;
}

#pragma warning restore IDE0022
