using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace RemoteFlow.Rdp.Windows.Interop;

/// <summary>A raw IDispatch sink. Dispatch IDs, not declaration order, identify MSTSCLib events.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class NativeRdpEventSink : IMsTscAxEventsSink, IDisposable
{
    private const int _success = 0;
    private const int _notImplemented = unchecked((int)0x80004001);
    private IConnectionPoint? _connectionPoint;
    private int _cookie;

    public event EventHandler<NativeRdpEventArgs>? EventReceived;

    public void Advise(object control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (control is not IConnectionPointContainer container)
        {
            throw new InvalidOperationException("The RDP control does not expose its event connection point.");
        }

        var eventInterface = typeof(IMsTscAxEventsSink).GUID;
        container.FindConnectionPoint(ref eventInterface, out var point);
        if (point is null)
        {
            throw new InvalidOperationException("The RDP control has no IMsTscAxEvents connection point.");
        }

        try
        {
            point.Advise(this, out _cookie);
            _connectionPoint = point;
        }
        catch
        {
            _ = Marshal.FinalReleaseComObject(point);
            throw;
        }
    }

    public int GetTypeInfoCount(out uint count)
    {
        count = 0;
        return _success;
    }

    public int GetTypeInfo(uint typeInfo, uint locale, out IntPtr pointer)
    {
        pointer = IntPtr.Zero;
        return _notImplemented;
    }

    public int GetIDsOfNames(in Guid interfaceId, IntPtr names, uint nameCount, uint locale, IntPtr dispatchIds)
    {
        return _notImplemented;
    }

    public int Invoke(
        int dispatchId,
        in Guid interfaceId,
        uint locale,
        ushort flags,
        IntPtr parameters,
        IntPtr result,
        IntPtr exceptionInfo,
        IntPtr argumentError)
    {
        try
        {
            EventReceived?.Invoke(this, new(dispatchId, ReadArguments(parameters)));
        }
        catch (Exception)
        {
            // A managed callback must never unwind through the unmanaged COM connection point.
        }

        return _success;
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
            // The control may already have torn down the connection point.
        }

        _ = Marshal.FinalReleaseComObject(_connectionPoint);
        _connectionPoint = null;
    }

    private static List<object?> ReadArguments(IntPtr parameters)
    {
        if (parameters == IntPtr.Zero)
        {
            return [];
        }

        var values = Marshal.PtrToStructure<DispatchParameters>(parameters);
        if (values.ArgumentCount == 0 || values.Arguments == IntPtr.Zero)
        {
            return [];
        }

        var variantSize = IntPtr.Size == 8 ? 24 : 16;
        var arguments = new List<object?>((int)values.ArgumentCount);
        for (var index = (int)values.ArgumentCount - 1; index >= 0; index--)
        {
            try
            {
                arguments.Add(Marshal.GetObjectForNativeVariant(values.Arguments + (index * variantSize)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                arguments.Add(null);
            }
        }

        return arguments;
    }
}

[ComImport]
[Guid("336d5562-efa8-482e-8cb3-c5c0fc7a7db6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsTscAxEventsSink
{
    [PreserveSig]
    int GetTypeInfoCount(out uint count);

    [PreserveSig]
    int GetTypeInfo(uint typeInfo, uint locale, out IntPtr pointer);

    [PreserveSig]
    int GetIDsOfNames(in Guid interfaceId, IntPtr names, uint nameCount, uint locale, IntPtr dispatchIds);

    [PreserveSig]
    int Invoke(
        int dispatchId,
        in Guid interfaceId,
        uint locale,
        ushort flags,
        IntPtr parameters,
        IntPtr result,
        IntPtr exceptionInfo,
        IntPtr argumentError);
}

[StructLayout(LayoutKind.Sequential)]
internal struct DispatchParameters
{
    public IntPtr Arguments;
    public IntPtr NamedArgumentDispatchIds;
    public uint ArgumentCount;
    public uint NamedArgumentCount;
}
