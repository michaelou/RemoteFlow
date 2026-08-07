using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Security;

public sealed class MacOsKeychainProvider : ICredentialProvider
{
    private const int _errSecSuccess = 0;
    private const int _errSecUserCanceled = -128;
    private const int _errSecDuplicateItem = -25299;
    private const int _errSecItemNotFound = -25300;
    private const int _errSecAuthFailed = -25293;
    private const string _serviceName = "io.remoteflow";

    public string Name => "macos-keychain";

    public bool IsAvailable => OperatingSystem.IsMacOS();

    public async Task<SecretHandle?> GetAsync(
        string storeKey,
        CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        EnsureMacOS();
        return await Task.Run(() => GetCore(storeKey), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAsync(
        string storeKey,
        ReadOnlyMemory<char> secret,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        EnsureMacOS();

        var secretCopy = secret.ToArray();
        try
        {
            await Task.Run(() => SetCore(storeKey, secretCopy), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(secretCopy.AsSpan()));
        }
    }

    public async Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        EnsureMacOS();
        await Task.Run(() => DeleteCore(storeKey), cancellationToken).ConfigureAwait(false);
    }

    private static SecretHandle? GetCore(string storeKey)
    {
        using var query = CreateIdentityQuery(storeKey);
        query.SetConstant(SecurityConstants.ReturnData, CoreFoundationConstants.BooleanTrue);
        query.SetConstant(SecurityConstants.MatchLimit, SecurityConstants.MatchLimitOne);

        var status = NativeMethods.SecItemCopyMatching(query.Handle, out var result);
        if (status == _errSecItemNotFound)
        {
            return null;
        }

        ThrowForStatus(status, "read");
        if (result == IntPtr.Zero)
        {
            throw new CredentialProviderException("macOS Keychain returned an empty result.");
        }

        try
        {
            var length = checked((int)NativeMethods.CFDataGetLength(result));
            var bytes = new byte[length];
            try
            {
                if (length > 0)
                {
                    Marshal.Copy(NativeMethods.CFDataGetBytePtr(result), bytes, 0, length);
                }

                var chars = new char[Encoding.UTF8.GetCharCount(bytes)];
                _ = Encoding.UTF8.GetChars(bytes, chars);
                try
                {
                    return new SecretHandle(chars);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            NativeMethods.CFRelease(result);
        }
    }

    private static void SetCore(string storeKey, ReadOnlySpan<char> secret)
    {
        var secretBytes = new byte[Encoding.UTF8.GetByteCount(secret)];
        try
        {
            _ = Encoding.UTF8.GetBytes(secret, secretBytes);
            using var attributes = new CfDictionary();
            attributes.SetOwnedData(SecurityConstants.ValueData, secretBytes);

            using var addQuery = CreateIdentityQuery(storeKey);
            addQuery.SetOwnedData(SecurityConstants.ValueData, secretBytes);
            var status = NativeMethods.SecItemAdd(addQuery.Handle, IntPtr.Zero);
            if (status == _errSecDuplicateItem)
            {
                using var identity = CreateIdentityQuery(storeKey);
                status = NativeMethods.SecItemUpdate(identity.Handle, attributes.Handle);
            }

            ThrowForStatus(status, "store");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    private static void DeleteCore(string storeKey)
    {
        using var query = CreateIdentityQuery(storeKey);
        var status = NativeMethods.SecItemDelete(query.Handle);
        if (status != _errSecItemNotFound)
        {
            ThrowForStatus(status, "delete");
        }
    }

    private static CfDictionary CreateIdentityQuery(string storeKey)
    {
        var query = new CfDictionary();
        try
        {
            query.SetConstant(SecurityConstants.Class, SecurityConstants.ClassGenericPassword);
            query.SetOwnedString(SecurityConstants.AttributeService, _serviceName);
            query.SetOwnedString(SecurityConstants.AttributeAccount, storeKey);
            return query;
        }
        catch
        {
            query.Dispose();
            throw;
        }
    }

    private static void ThrowForStatus(int status, string operation)
    {
        if (status == _errSecSuccess)
        {
            return;
        }

        if (status is _errSecUserCanceled or _errSecAuthFailed)
        {
            throw new CredentialAccessDeclinedException(
                $"The macOS Keychain did not authorize the credential {operation} operation.");
        }

        throw new CredentialProviderException(
            $"macOS Keychain could not {operation} the credential (status {status}).");
    }

    private static void ValidateStoreKey(string storeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);
    }

    private static void EnsureMacOS()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("macOS Keychain is only available on macOS.");
        }
    }

    private sealed class CfDictionary : IDisposable
    {
        private IntPtr _handle;

        internal CfDictionary()
        {
            _handle = NativeMethods.CFDictionaryCreateMutable(
                IntPtr.Zero,
                0,
                CoreFoundationConstants.TypeDictionaryKeyCallbacks,
                CoreFoundationConstants.TypeDictionaryValueCallbacks);
            if (_handle == IntPtr.Zero)
            {
                throw new CredentialProviderException("CoreFoundation could not create a Keychain query.");
            }
        }

        internal IntPtr Handle => _handle != IntPtr.Zero
            ? _handle
            : throw new ObjectDisposedException(nameof(CfDictionary));

        internal void SetConstant(IntPtr key, IntPtr value)
        {
            NativeMethods.CFDictionarySetValue(Handle, key, value);
        }

        internal void SetOwnedString(IntPtr key, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try
            {
                var nativeValue = NativeMethods.CFStringCreateWithBytes(
                    IntPtr.Zero,
                    bytes,
                    bytes.Length,
                    CoreFoundationConstants.Utf8Encoding,
                    false);
                SetOwnedValue(key, nativeValue);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        internal void SetOwnedData(IntPtr key, byte[] value)
        {
            SetOwnedValue(key, NativeMethods.CFDataCreate(IntPtr.Zero, value, value.Length));
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CFRelease(handle);
            }
        }

        private void SetOwnedValue(IntPtr key, IntPtr value)
        {
            if (value == IntPtr.Zero)
            {
                throw new CredentialProviderException("CoreFoundation could not create a Keychain query value.");
            }

            try
            {
                NativeMethods.CFDictionarySetValue(Handle, key, value);
            }
            finally
            {
                NativeMethods.CFRelease(value);
            }
        }
    }

    private static class SecurityConstants
    {
        internal static IntPtr Class => NativeConstant(NativeLibraries.Security, "kSecClass");
        internal static IntPtr ClassGenericPassword => NativeConstant(NativeLibraries.Security, "kSecClassGenericPassword");
        internal static IntPtr AttributeService => NativeConstant(NativeLibraries.Security, "kSecAttrService");
        internal static IntPtr AttributeAccount => NativeConstant(NativeLibraries.Security, "kSecAttrAccount");
        internal static IntPtr ValueData => NativeConstant(NativeLibraries.Security, "kSecValueData");
        internal static IntPtr ReturnData => NativeConstant(NativeLibraries.Security, "kSecReturnData");
        internal static IntPtr MatchLimit => NativeConstant(NativeLibraries.Security, "kSecMatchLimit");
        internal static IntPtr MatchLimitOne => NativeConstant(NativeLibraries.Security, "kSecMatchLimitOne");
    }

    private static class CoreFoundationConstants
    {
        internal const uint Utf8Encoding = 0x08000100;

        internal static IntPtr BooleanTrue => NativeConstant(NativeLibraries.CoreFoundation, "kCFBooleanTrue");
        internal static IntPtr TypeDictionaryKeyCallbacks =>
            NativeLibrary.GetExport(NativeLibraries.CoreFoundation, "kCFTypeDictionaryKeyCallBacks");
        internal static IntPtr TypeDictionaryValueCallbacks =>
            NativeLibrary.GetExport(NativeLibraries.CoreFoundation, "kCFTypeDictionaryValueCallBacks");
    }

    private static class NativeLibraries
    {
        internal static IntPtr Security { get; } = NativeLibrary.Load(_securityFramework);
        internal static IntPtr CoreFoundation { get; } = NativeLibrary.Load(_coreFoundationFramework);
    }

    private static IntPtr NativeConstant(IntPtr library, string name)
    {
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }

    private const string _securityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string _coreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private static class NativeMethods
    {
#pragma warning disable SYSLIB1054
        [DllImport(_securityFramework)]
        internal static extern int SecItemAdd(IntPtr attributes, IntPtr result);

        [DllImport(_securityFramework)]
        internal static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

        [DllImport(_securityFramework)]
        internal static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);

        [DllImport(_securityFramework)]
        internal static extern int SecItemDelete(IntPtr query);

        [DllImport(_coreFoundationFramework)]
        internal static extern IntPtr CFDictionaryCreateMutable(
            IntPtr allocator,
            nint capacity,
            IntPtr keyCallbacks,
            IntPtr valueCallbacks);

        [DllImport(_coreFoundationFramework)]
        internal static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);

        [DllImport(_coreFoundationFramework)]
        internal static extern IntPtr CFStringCreateWithBytes(
            IntPtr allocator,
            byte[] bytes,
            nint length,
            uint encoding,
            [MarshalAs(UnmanagedType.I1)] bool isExternalRepresentation);

        [DllImport(_coreFoundationFramework)]
        internal static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

        [DllImport(_coreFoundationFramework)]
        internal static extern nint CFDataGetLength(IntPtr data);

        [DllImport(_coreFoundationFramework)]
        internal static extern IntPtr CFDataGetBytePtr(IntPtr data);

        [DllImport(_coreFoundationFramework)]
        internal static extern void CFRelease(IntPtr value);
#pragma warning restore SYSLIB1054
    }
}
