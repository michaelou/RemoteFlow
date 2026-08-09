using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RemoteFlow.Rdp.Windows.Interop;

internal sealed class WindowsNativeRdpControlFactory : INativeRdpControlFactory
{
    private static readonly (int Generation, Guid ClassId)[] _candidates =
    [
        (12, new("3f859aa3-c2d4-4faa-b0e4-fd0c9c4e5e3a")),
        (11, new("1df7c823-b2d4-4b54-975a-f2ac5d7cf8b8")),
        (10, new("a0c63c30-f08d-4ab4-907c-34905d770c7d")),
        (9, new("8b918b82-7985-4c24-89df-c33ad2bbfbcd")),
        (8, new("a3bc03a0-041d-42e3-ad22-882b7865c9c5")),
    ];

    public static WindowsNativeRdpControlFactory Instance { get; } = new();

    private WindowsNativeRdpControlFactory()
    {
    }

    public INativeRdpControl Create(RdpControlSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        Exception? lastFailure = null;

        foreach (var (generation, classId) in _candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? instance = null;
            try
            {
                var type = Type.GetTypeFromCLSID(classId, throwOnError: true)!;
                instance = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException($"MsRdpClient{generation} returned no COM object.");
                var ownedInstance = instance;
                instance = null;
                return new WindowsNativeRdpControl(ownedInstance, settings);
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException or TargetInvocationException)
            {
                lastFailure = exception;
                if (instance is not null && Marshal.IsComObject(instance))
                {
                    _ = Marshal.FinalReleaseComObject(instance);
                }
            }
        }

        throw new InvalidOperationException(
            "No supported Microsoft Remote Desktop ActiveX control could be activated.",
            lastFailure);
    }
}

internal sealed class WindowsNativeRdpControl : INativeRdpControl
{
    private const BindingFlags _dispatchFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
    private readonly NativeRdpEventSink _eventSink = new();
    private object? _instance;
    private object? _advancedSettings;
    private IMsRdpClientNonScriptable5? _nonScriptable;
    private int _disposed;

    public WindowsNativeRdpControl(object instance, RdpControlSettings settings)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            _nonScriptable = instance as IMsRdpClientNonScriptable5
                ?? throw new InvalidOperationException("The RDP control does not expose credential handover.");
            _eventSink.EventReceived += ForwardEvent;
            _eventSink.Advise(instance);
            Apply(settings);
        }
        catch
        {
            DisposeCore();
            throw;
        }
    }

    public event EventHandler<NativeRdpEventArgs>? EventReceived;

    public object NativeInstance => _instance ?? throw new ObjectDisposedException(nameof(WindowsNativeRdpControl));

    public uint ExtendedDisconnectReason
    {
        get
        {
            try
            {
                var value = GetProperty(RequiredInstance(), "ExtendedDisconnectReason");
                return unchecked((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    public void Connect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = InvokeMethod(RequiredInstance(), "Connect");
    }

    public void Disconnect(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = InvokeMethod(RequiredInstance(), "Disconnect");
    }

    public void ConfigureCredentialPolicy(bool allowCredentialSaving, bool allowPromptingForCredentials)
    {
        var control = RequiredNonScriptable();
        Marshal.ThrowExceptionForHR(control.put_AllowCredentialSaving(VariantBool(allowCredentialSaving)));
        Marshal.ThrowExceptionForHR(control.put_AllowPromptingForCredentials(VariantBool(allowPromptingForCredentials)));
    }

    public void SetClearTextPassword(ReadOnlySpan<char> password)
    {
        // COM requires a BSTR, so one short-lived managed string is unavoidable and cannot be reliably
        // zeroed. Keep it scoped to this single write-only assignment and never retain or log it.
        Marshal.ThrowExceptionForHR(RequiredNonScriptable().put_ClearTextPassword(new string(password)));
    }

    public void ResetPassword()
    {
        Marshal.ThrowExceptionForHR(RequiredNonScriptable().ResetPassword());
    }

    public NativeRdpResizeResult UpdateSessionDisplaySettings(
        int width,
        int height,
        uint desktopScaleFactor,
        uint deviceScaleFactor)
    {
        try
        {
            _ = InvokeMethod(
                RequiredInstance(),
                "UpdateSessionDisplaySettings",
                checked((uint)width),
                checked((uint)height),
                0u,
                0u,
                0u,
                desktopScaleFactor,
                deviceScaleFactor);
            return NativeRdpResizeResult.Success;
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException or
            MissingMethodException or ArgumentException or OverflowException)
        {
            return ResizeFailure(exception);
        }
    }

    public NativeRdpResizeResult SetSmartSizing(bool enabled)
    {
        try
        {
            SetProperty(
                _advancedSettings ?? throw new ObjectDisposedException(nameof(WindowsNativeRdpControl)),
                "SmartSizing",
                enabled);
            return NativeRdpResizeResult.Success;
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException or
            MissingMethodException or ArgumentException or ObjectDisposedException)
        {
            return ResizeFailure(exception);
        }
    }

    public string DescribeDisconnect(uint disconnectReason, uint extendedDisconnectReason)
    {
        try
        {
            return Convert.ToString(
                InvokeMethod(RequiredInstance(), "GetErrorDescription", disconnectReason, extendedDisconnectReason),
                CultureInfo.InvariantCulture) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private void Apply(RdpControlSettings settings)
    {
        var instance = RequiredInstance();
        SetProperty(instance, "Server", settings.Server);
        SetProperty(instance, "DesktopWidth", settings.DesktopWidth);
        SetProperty(instance, "DesktopHeight", settings.DesktopHeight);
        SetProperty(instance, "ColorDepth", settings.ColorDepth);
        if (!string.IsNullOrWhiteSpace(settings.UserName))
        {
            SetProperty(instance, "UserName", settings.UserName);
        }
        if (!string.IsNullOrWhiteSpace(settings.Domain))
        {
            SetProperty(instance, "Domain", settings.Domain);
        }

        _advancedSettings = GetProperty(instance, "AdvancedSettings9")
            ?? throw new InvalidOperationException("The RDP control does not expose AdvancedSettings9.");
        SetProperty(_advancedSettings, "RDPPort", settings.RdpPort);
        SetProperty(_advancedSettings, "RedirectClipboard", settings.AdvancedSettings.RedirectClipboard);
        SetProperty(_advancedSettings, "RedirectDrives", settings.AdvancedSettings.RedirectDrives);
        SetProperty(_advancedSettings, "AuthenticationLevel", settings.AdvancedSettings.AuthenticationLevel);
        SetProperty(_advancedSettings, "EnableCredSspSupport", settings.AdvancedSettings.EnableCredSspSupport);
        SetProperty(_advancedSettings, "SmartSizing", settings.AdvancedSettings.SmartSizing);
        // KeyboardHookMode belongs to the secured-settings surface and is applied with #88's focus policy.
    }

    private void ForwardEvent(object? sender, NativeRdpEventArgs e)
    {
        try
        {
            EventReceived?.Invoke(this, e);
        }
        catch (Exception)
        {
            // The raw COM sink also guards this boundary; keep the adapter safe when invoked directly.
        }
    }

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _eventSink.EventReceived -= ForwardEvent;
        _eventSink.Dispose();

        var advanced = Interlocked.Exchange(ref _advancedSettings, null);
        if (advanced is not null && Marshal.IsComObject(advanced))
        {
            _ = Marshal.FinalReleaseComObject(advanced);
        }

        // This is a QueryInterface view of the control's own RCW, not a separate COM identity.
        _nonScriptable = null;

        var instance = Interlocked.Exchange(ref _instance, null);
        if (instance is not null && Marshal.IsComObject(instance))
        {
            _ = Marshal.FinalReleaseComObject(instance);
        }
    }

    private object RequiredInstance()
    {
        return _instance ?? throw new ObjectDisposedException(nameof(WindowsNativeRdpControl));
    }

    private IMsRdpClientNonScriptable5 RequiredNonScriptable()
    {
        return _nonScriptable ?? throw new ObjectDisposedException(nameof(WindowsNativeRdpControl));
    }

    private static short VariantBool(bool value)
    {
        return value ? (short)-1 : (short)0;
    }

    private static NativeRdpResizeResult ResizeFailure(Exception exception)
    {
        var cause = exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception;
        return NativeRdpResizeResult.Failure(
            $"{cause.GetType().Name} (HRESULT 0x{Marshal.GetHRForException(cause):X8})");
    }

    private static object? GetProperty(object target, string name)
    {
        return target.GetType().InvokeMember(
            name,
            _dispatchFlags | BindingFlags.GetProperty,
            binder: null,
            target,
            args: null,
            CultureInfo.InvariantCulture);
    }

    private static void SetProperty(object target, string name, object? value)
    {
        _ = target.GetType().InvokeMember(
            name,
            _dispatchFlags | BindingFlags.SetProperty,
            binder: null,
            target,
            [value],
            CultureInfo.InvariantCulture);
    }

    private static object? InvokeMethod(object target, string name, params object?[] arguments)
    {
        return target.GetType().InvokeMember(
            name,
            _dispatchFlags | BindingFlags.InvokeMethod,
            binder: null,
            target,
            arguments,
            CultureInfo.InvariantCulture);
    }
}
