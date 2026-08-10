using System.Runtime.InteropServices;

namespace RemoteFlow.Rdp.Windows.Interop;

internal static class OleHosting
{
    public const int Success = 0;
    public const int False = 1;
    public const int NotImplemented = unchecked((int)0x80004001);
    public const int NoInterface = unchecked((int)0x80004002);
    public const int MemberNotFound = unchecked((int)0x80020003);
    public const int InPlaceActivateVerb = -5;
    public const uint CloseNoSave = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect(int left, int top, int right, int bottom)
{
    public int Left = left;
    public int Top = top;
    public int Right = right;
    public int Bottom = bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeSize
{
    public int Width;
    public int Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFloatPoint
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMessage
{
    public IntPtr Window;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public NativePoint Point;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OleFrameInfo
{
    public uint Size;
    public int IsMdiApplication;
    public IntPtr FrameWindow;
    public IntPtr AcceleratorTable;
    public uint AcceleratorCount;
}

[ComImport]
[Guid("00000112-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleObject
{
    [PreserveSig] int SetClientSite(IOleClientSite? clientSite);
    [PreserveSig] int GetClientSite(out IntPtr clientSite);
    [PreserveSig] int SetHostNames([MarshalAs(UnmanagedType.LPWStr)] string containerApp, [MarshalAs(UnmanagedType.LPWStr)] string? containerObject);
    [PreserveSig] int Close(uint saveOption);
    [PreserveSig] int SetMoniker(uint whichMoniker, IntPtr moniker);
    [PreserveSig] int GetMoniker(uint assign, uint whichMoniker, out IntPtr moniker);
    [PreserveSig] int InitFromData(IntPtr dataObject, int creation, uint reserved);
    [PreserveSig] int GetClipboardData(uint reserved, out IntPtr dataObject);
    [PreserveSig] int DoVerb(int verb, IntPtr message, IOleClientSite? activeSite, int index, IntPtr parent, in NativeRect position);
    [PreserveSig] int EnumVerbs(out IntPtr verbs);
    [PreserveSig] int Update();
    [PreserveSig] int IsUpToDate();
    [PreserveSig] int GetUserClassID(out Guid classId);
    [PreserveSig] int GetUserType(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string userType);
    [PreserveSig] int SetExtent(uint drawAspect, in NativeSize size);
    [PreserveSig] int GetExtent(uint drawAspect, out NativeSize size);
    [PreserveSig] int Advise(IntPtr adviseSink, out uint connection);
    [PreserveSig] int Unadvise(uint connection);
    [PreserveSig] int EnumAdvise(out IntPtr adviseEnumerator);
    [PreserveSig] int GetMiscStatus(uint aspect, out uint status);
    [PreserveSig] int SetColorScheme(IntPtr palette);
}

[ComImport]
[Guid("00000113-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleInPlaceObject
{
    [PreserveSig] int GetWindow(out IntPtr window);
    [PreserveSig] int ContextSensitiveHelp(int enterMode);
    [PreserveSig] int InPlaceDeactivate();
    [PreserveSig] int UIDeactivate();
    [PreserveSig] int SetObjectRects(in NativeRect position, in NativeRect clip);
    [PreserveSig] int ReactivateAndUndo();
}

[ComImport]
[Guid("00000118-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleClientSite
{
    [PreserveSig] int SaveObject();
    [PreserveSig] int GetMoniker(uint assign, uint whichMoniker, out IntPtr moniker);
    [PreserveSig] int GetContainer(out IntPtr container);
    [PreserveSig] int ShowObject();
    [PreserveSig] int OnShowWindow(int show);
    [PreserveSig] int RequestNewObjectLayout();
}

[ComImport]
[Guid("00000119-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleInPlaceSite
{
    [PreserveSig] int GetWindow(out IntPtr window);
    [PreserveSig] int ContextSensitiveHelp(int enterMode);
    [PreserveSig] int CanInPlaceActivate();
    [PreserveSig] int OnInPlaceActivate();
    [PreserveSig] int OnUIActivate();
    [PreserveSig] int GetWindowContext(out IntPtr frame, out IntPtr document, out NativeRect position, out NativeRect clip, ref OleFrameInfo frameInfo);
    [PreserveSig] int Scroll(NativeSize scrollExtent);
    [PreserveSig] int OnUIDeactivate(int undoable);
    [PreserveSig] int OnInPlaceDeactivate();
    [PreserveSig] int DiscardUndoState();
    [PreserveSig] int DeactivateAndUndo();
    [PreserveSig] int OnPosRectChange(in NativeRect position);
}

[ComImport]
[Guid("00000116-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleInPlaceFrame
{
    [PreserveSig] int GetWindow(out IntPtr window);
    [PreserveSig] int ContextSensitiveHelp(int enterMode);
    [PreserveSig] int GetBorder(out NativeRect border);
    [PreserveSig] int RequestBorderSpace(in NativeRect borderWidths);
    [PreserveSig] int SetBorderSpace(in NativeRect borderWidths);
    [PreserveSig] int SetActiveObject(IntPtr activeObject, [MarshalAs(UnmanagedType.LPWStr)] string? objectName);
    [PreserveSig] int InsertMenus(IntPtr sharedMenu, IntPtr menuWidths);
    [PreserveSig] int SetMenu(IntPtr sharedMenu, IntPtr oleMenu, IntPtr activeObjectWindow);
    [PreserveSig] int RemoveMenus(IntPtr sharedMenu);
    [PreserveSig] int SetStatusText([MarshalAs(UnmanagedType.LPWStr)] string? statusText);
    [PreserveSig] int EnableModeless(int enable);
    [PreserveSig] int TranslateAccelerator(in NativeMessage message, ushort commandId);
}

[ComImport]
[Guid("B196B289-BAB4-101A-B69C-00AA00341D07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleControlSite
{
    [PreserveSig] int OnControlInfoChanged();
    [PreserveSig] int LockInPlaceActive(int locked);
    [PreserveSig] int GetExtendedControl(out IntPtr dispatch);
    [PreserveSig] int TransformCoords(ref NativePoint himetric, ref NativeFloatPoint container, uint flags);
    [PreserveSig] int TranslateAccelerator(in NativeMessage message, uint modifiers);
    [PreserveSig] int OnFocus(int gotFocus);
    [PreserveSig] int ShowPropertyFrame();
}

[ComImport]
[Guid("00020400-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDispatchSite
{
    [PreserveSig] int GetTypeInfoCount(out uint count);
    [PreserveSig] int GetTypeInfo(uint typeInfo, uint locale, out IntPtr pointer);
    [PreserveSig] int GetIDsOfNames(in Guid interfaceId, IntPtr names, uint nameCount, uint locale, IntPtr dispatchIds);
    [PreserveSig] int Invoke(int dispatchId, in Guid interfaceId, uint locale, ushort flags, IntPtr parameters, IntPtr result, IntPtr exceptionInfo, IntPtr argumentError);
}
