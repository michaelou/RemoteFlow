using System.Runtime.InteropServices;

#pragma warning disable IDE1006 // Native member names preserve the type-library transcription and vtable audit trail.

namespace RemoteFlow.Rdp.Windows.Interop;

// Transcribed from the MSTSCLib type library. The inherited IUnknown slots are flattened and must stay
// in this exact order; a slot mismatch is memory corruption, not a managed exception.
[ComImport]
[Guid("4f6996d5-d7b1-412c-b0ff-063718566907")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMsRdpClientNonScriptable5
{
    [PreserveSig] int put_ClearTextPassword([MarshalAs(UnmanagedType.BStr)] string value);
    [PreserveSig] int put_PortablePassword([MarshalAs(UnmanagedType.BStr)] string value);
    [PreserveSig] int get_PortablePassword([MarshalAs(UnmanagedType.BStr)] out string value);
    [PreserveSig] int put_PortableSalt([MarshalAs(UnmanagedType.BStr)] string value);
    [PreserveSig] int get_PortableSalt([MarshalAs(UnmanagedType.BStr)] out string value);
    [PreserveSig] int put_BinaryPassword([MarshalAs(UnmanagedType.BStr)] string value);
    [PreserveSig] int get_BinaryPassword([MarshalAs(UnmanagedType.BStr)] out string value);
    [PreserveSig] int put_BinarySalt([MarshalAs(UnmanagedType.BStr)] string value);
    [PreserveSig] int get_BinarySalt([MarshalAs(UnmanagedType.BStr)] out string value);
    [PreserveSig] int ResetPassword();
    [PreserveSig] int NotifyRedirectDeviceChange(int wParam, int lParam);
    [PreserveSig] int SendKeys(int keyCount, in short keysUp, in int keyData);
    [PreserveSig] int put_UIParentWindowHandle(int windowHandle);
    [PreserveSig] int get_UIParentWindowHandle(out int windowHandle);
    [PreserveSig] int put_ShowRedirectionWarningDialog(short show);
    [PreserveSig] int get_ShowRedirectionWarningDialog(out short show);
    [PreserveSig] int put_PromptForCredentials(short prompt);
    [PreserveSig] int get_PromptForCredentials(out short prompt);
    [PreserveSig] int put_NegotiateSecurityLayer(short negotiate);
    [PreserveSig] int get_NegotiateSecurityLayer(out short negotiate);
    [PreserveSig] int put_EnableCredSspSupport(short enabled);
    [PreserveSig] int get_EnableCredSspSupport(out short enabled);
    [PreserveSig] int put_RedirectDynamicDrives(short redirect);
    [PreserveSig] int get_RedirectDynamicDrives(out short redirect);
    [PreserveSig] int put_RedirectDynamicDevices(short redirect);
    [PreserveSig] int get_RedirectDynamicDevices(out short redirect);
    [PreserveSig] int get_DeviceCollection(out IntPtr collection);
    [PreserveSig] int get_DriveCollection(out IntPtr collection);
    [PreserveSig] int put_WarnAboutSendingCredentials(short warn);
    [PreserveSig] int get_WarnAboutSendingCredentials(out short warn);
    [PreserveSig] int put_WarnAboutClipboardRedirection(short warn);
    [PreserveSig] int get_WarnAboutClipboardRedirection(out short warn);
    [PreserveSig] int put_ConnectionBarText([MarshalAs(UnmanagedType.BStr)] string value);
    [PreserveSig] int get_ConnectionBarText([MarshalAs(UnmanagedType.BStr)] out string value);
    [PreserveSig] int put_RedirectionWarningType(int warningType);
    [PreserveSig] int get_RedirectionWarningType(out int warningType);
    [PreserveSig] int put_MarkRdpSettingsSecure(short secure);
    [PreserveSig] int get_MarkRdpSettingsSecure(out short secure);
    [PreserveSig] int put_PublisherCertificateChain([MarshalAs(UnmanagedType.Struct)] in object certificate);
    [PreserveSig] int get_PublisherCertificateChain([MarshalAs(UnmanagedType.Struct)] out object certificate);
    [PreserveSig] int put_WarnAboutPrinterRedirection(short warn);
    [PreserveSig] int get_WarnAboutPrinterRedirection(out short warn);
    [PreserveSig] int put_AllowCredentialSaving(short allow);
    [PreserveSig] int get_AllowCredentialSaving(out short allow);
    [PreserveSig] int put_PromptForCredsOnClient(short prompt);
    [PreserveSig] int get_PromptForCredsOnClient(out short prompt);
    [PreserveSig] int put_LaunchedViaClientShellInterface(short launched);
    [PreserveSig] int get_LaunchedViaClientShellInterface(out short launched);
    [PreserveSig] int put_TrustedZoneSite(short trusted);
    [PreserveSig] int get_TrustedZoneSite(out short trusted);
    [PreserveSig] int put_UseMultimon(short useMultiMonitor);
    [PreserveSig] int get_UseMultimon(out short useMultiMonitor);
    [PreserveSig] int get_RemoteMonitorCount(out uint monitorCount);
    [PreserveSig] int GetRemoteMonitorsBoundingBox(out int left, out int top, out int right, out int bottom);
    [PreserveSig] int get_RemoteMonitorLayoutMatchesLocal(out short matches);
    [PreserveSig] int put_DisableConnectionBar(short disable);
    [PreserveSig] int put_DisableRemoteAppCapsCheck(short disable);
    [PreserveSig] int get_DisableRemoteAppCapsCheck(out short disable);
    [PreserveSig] int put_WarnAboutDirectXRedirection(short warn);
    [PreserveSig] int get_WarnAboutDirectXRedirection(out short warn);
    [PreserveSig] int put_AllowPromptingForCredentials(short allow);
    [PreserveSig] int get_AllowPromptingForCredentials(out short allow);
}

#pragma warning restore IDE1006
