using System.Runtime.InteropServices;

namespace RemoteFlow.RdpSpike.Interop;

/// <summary>The OLE embedding surface an ActiveX control expects from its container, declared by hand.
/// This is the part of the interop that is genuinely small and genuinely stable — these interfaces have
/// not changed since OLE 2 — which is why ADR-0017 keeps it as source rather than generating it.</summary>
internal static class Ole
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    public const int REGDB_E_CLASSNOTREG = unchecked((int)0x80040154);

    public const int OLEIVERB_SHOW = -1;
    public const int OLEIVERB_INPLACEACTIVATE = -5;
    public const int OLEIVERB_UIACTIVATE = -4;

    public const uint OLECLOSE_NOSAVE = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect(int left, int top, int right, int bottom)
{
    public int Left = left;
    public int Top = top;
    public int Right = right;
    public int Bottom = bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SizeL
{
    public int Cx;
    public int Cy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointL
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PointF32
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public PointL Point;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OleInPlaceFrameInfo
{
    public uint Cb;
    public int FMdiApp;
    public IntPtr HwndFrame;
    public IntPtr Haccel;
    public uint CAccelEntries;
}

[ComImport]
[Guid("00000112-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleObject
{
    [PreserveSig] int SetClientSite(IOleClientSite? pClientSite);
    [PreserveSig] int GetClientSite(out IntPtr ppClientSite);
    [PreserveSig] int SetHostNames([MarshalAs(UnmanagedType.LPWStr)] string szContainerApp, [MarshalAs(UnmanagedType.LPWStr)] string? szContainerObj);
    [PreserveSig] int Close(uint dwSaveOption);
    [PreserveSig] int SetMoniker(uint dwWhichMoniker, IntPtr pmk);
    [PreserveSig] int GetMoniker(uint dwAssign, uint dwWhichMoniker, out IntPtr ppmk);
    [PreserveSig] int InitFromData(IntPtr pDataObject, int fCreation, uint dwReserved);
    [PreserveSig] int GetClipboardData(uint dwReserved, out IntPtr ppDataObject);
    [PreserveSig] int DoVerb(int iVerb, IntPtr lpmsg, IOleClientSite? pActiveSite, int lindex, IntPtr hwndParent, in Rect lprcPosRect);
    [PreserveSig] int EnumVerbs(out IntPtr ppEnumOleVerb);
    [PreserveSig] int Update();
    [PreserveSig] int IsUpToDate();
    [PreserveSig] int GetUserClassID(out Guid pClsid);
    [PreserveSig] int GetUserType(uint dwFormOfType, [MarshalAs(UnmanagedType.LPWStr)] out string pszUserType);
    [PreserveSig] int SetExtent(uint dwDrawAspect, in SizeL psizel);
    [PreserveSig] int GetExtent(uint dwDrawAspect, out SizeL psizel);
    [PreserveSig] int Advise(IntPtr pAdvSink, out uint pdwConnection);
    [PreserveSig] int Unadvise(uint dwConnection);
    [PreserveSig] int EnumAdvise(out IntPtr ppenumAdvise);
    [PreserveSig] int GetMiscStatus(uint dwAspect, out uint pdwStatus);
    [PreserveSig] int SetColorScheme(IntPtr pLogpal);
}

[ComImport]
[Guid("00000113-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleInPlaceObject
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
    [PreserveSig] int InPlaceDeactivate();
    [PreserveSig] int UIDeactivate();
    [PreserveSig] int SetObjectRects(in Rect lprcPosRect, in Rect lprcClipRect);
    [PreserveSig] int ReactivateAndUndo();
}

[ComImport]
[Guid("00000117-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleInPlaceActiveObject
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
    [PreserveSig] int TranslateAccelerator(in Msg lpmsg);
    [PreserveSig] int OnFrameWindowActivate(int fActivate);
    [PreserveSig] int OnDocWindowActivate(int fActivate);
    [PreserveSig] int ResizeBorder(in Rect prcBorder, IntPtr pUIWindow, int fFrameWindow);
    [PreserveSig] int EnableModeless(int fEnable);
}

[ComImport]
[Guid("B196B288-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleControl
{
    [PreserveSig] int GetControlInfo(IntPtr pCI);
    [PreserveSig] int OnMnemonic(in Msg pMsg);
    [PreserveSig] int OnAmbientPropertyChange(int dispID);
    [PreserveSig] int FreezeEvents(int bFreeze);
}

[ComImport]
[Guid("00000118-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleClientSite
{
    [PreserveSig] int SaveObject();
    [PreserveSig] int GetMoniker(uint dwAssign, uint dwWhichMoniker, out IntPtr ppmk);
    [PreserveSig] int GetContainer(out IntPtr ppContainer);
    [PreserveSig] int ShowObject();
    [PreserveSig] int OnShowWindow(int fShow);
    [PreserveSig] int RequestNewObjectLayout();
}

[ComImport]
[Guid("00000119-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleInPlaceSite
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
    [PreserveSig] int CanInPlaceActivate();
    [PreserveSig] int OnInPlaceActivate();
    [PreserveSig] int OnUIActivate();
    [PreserveSig] int GetWindowContext(out IntPtr ppFrame, out IntPtr ppDoc, out Rect lprcPosRect, out Rect lprcClipRect, ref OleInPlaceFrameInfo lpFrameInfo);
    [PreserveSig] int Scroll(SizeL scrollExtant);
    [PreserveSig] int OnUIDeactivate(int fUndoable);
    [PreserveSig] int OnInPlaceDeactivate();
    [PreserveSig] int DiscardUndoState();
    [PreserveSig] int DeactivateAndUndo();
    [PreserveSig] int OnPosRectChange(in Rect lprcPosRect);
}

[ComImport]
[Guid("00000116-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleInPlaceFrame
{
    [PreserveSig] int GetWindow(out IntPtr phwnd);
    [PreserveSig] int ContextSensitiveHelp(int fEnterMode);
    [PreserveSig] int GetBorder(out Rect lprectBorder);
    [PreserveSig] int RequestBorderSpace(in Rect pborderwidths);
    [PreserveSig] int SetBorderSpace(in Rect pborderwidths);
    [PreserveSig] int SetActiveObject(IntPtr pActiveObject, [MarshalAs(UnmanagedType.LPWStr)] string? pszObjName);
    [PreserveSig] int InsertMenus(IntPtr hmenuShared, IntPtr lpMenuWidths);
    [PreserveSig] int SetMenu(IntPtr hmenuShared, IntPtr holemenu, IntPtr hwndActiveObject);
    [PreserveSig] int RemoveMenus(IntPtr hmenuShared);
    [PreserveSig] int SetStatusText([MarshalAs(UnmanagedType.LPWStr)] string? pszStatusText);
    [PreserveSig] int EnableModeless(int fEnable);
    [PreserveSig] int TranslateAccelerator(in Msg lpmsg, ushort wID);
}

[ComImport]
[Guid("B196B289-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleControlSite
{
    [PreserveSig] int OnControlInfoChanged();
    [PreserveSig] int LockInPlaceActive(int fLock);
    [PreserveSig] int GetExtendedControl(out IntPtr ppDisp);
    [PreserveSig] int TransformCoords(ref PointL pPtlHimetric, ref PointF32 pPtfContainer, uint dwFlags);
    [PreserveSig] int TranslateAccelerator(in Msg pMsg, uint grfModifiers);
    [PreserveSig] int OnFocus(int fGotFocus);
    [PreserveSig] int ShowPropertyFrame();
}

/// <summary>Declared so the container can answer ambient-property queries itself. A managed class that
/// implements this alongside the site interfaces gets one CCW that satisfies every QueryInterface the
/// control makes during activation.</summary>
[ComImport]
[Guid("00020400-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDispatchSite
{
    [PreserveSig] int GetTypeInfoCount(out uint pctinfo);
    [PreserveSig] int GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    [PreserveSig] int GetIDsOfNames(in Guid riid, IntPtr rgszNames, uint cNames, uint lcid, IntPtr rgdispid);
    [PreserveSig] int Invoke(int dispid, in Guid riid, uint lcid, ushort flags, IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
}

/// <summary>The MSTSCLib event dispinterface, declared as an IUnknown-shaped IDispatch so a managed class
/// can be advised as the sink: the connection point only ever calls slots 3-6, and only ever Invoke.</summary>
[ComImport]
[Guid("336d5562-efa8-482e-8cb3-c5c0fc7a7db6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscAxEventsSink
{
    [PreserveSig] int GetTypeInfoCount(out uint pctinfo);
    [PreserveSig] int GetTypeInfo(uint itinfo, uint lcid, out IntPtr pptinfo);
    [PreserveSig] int GetIDsOfNames(in Guid riid, IntPtr rgszNames, uint cNames, uint lcid, IntPtr rgdispid);
    [PreserveSig] int Invoke(int dispid, in Guid riid, uint lcid, ushort flags, IntPtr pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);
}

[StructLayout(LayoutKind.Sequential)]
internal struct DispParams
{
    public IntPtr Rgvarg;
    public IntPtr RgdispidNamedArgs;
    public uint CArgs;
    public uint CNamedArgs;
}
