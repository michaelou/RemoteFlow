using System.Runtime.InteropServices;

#pragma warning disable IDE1006 // Native member names preserve the type-library transcription.

namespace RemoteFlow.Rdp.Windows.Interop;

[ComImport]
[Guid("302d8188-0052-4807-806a-362b628f9ac5")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpExtendedSettings
{
    [PreserveSig]
    int put_Property(
        [MarshalAs(UnmanagedType.BStr)] string propertyName,
        [MarshalAs(UnmanagedType.Struct)] in object value);

    [PreserveSig]
    int get_Property(
        [MarshalAs(UnmanagedType.BStr)] string propertyName,
        [MarshalAs(UnmanagedType.Struct)] out object value);
}

#pragma warning restore IDE1006
