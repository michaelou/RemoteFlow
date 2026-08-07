using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Security;

public sealed class LibSecretProvider : ICredentialProvider, IDisposable
{
    private readonly LibSecretNative? _native = LibSecretNative.TryCreate();
    private bool _disposed;

    public string Name => "libsecret";

    public bool IsAvailable => !_disposed && _native is not null;

    public async Task<SecretHandle?> GetAsync(
        string storeKey,
        CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        var native = RequireNative();
        return await Task.Run(() => native.Get(storeKey), cancellationToken).ConfigureAwait(false);
    }

    public async Task SetAsync(
        string storeKey,
        ReadOnlyMemory<char> secret,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var native = RequireNative();
        var secretCopy = secret.ToArray();
        try
        {
            await Task.Run(() => native.Set(storeKey, secretCopy, displayName), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(secretCopy.AsSpan()));
        }
    }

    public async Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        var native = RequireNative();
        await Task.Run(() => native.Delete(storeKey), cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _native?.Dispose();
        _disposed = true;
    }

    private LibSecretNative RequireNative()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _native ?? throw new CredentialProviderException("The libsecret credential provider is unavailable.");
    }

    private static void ValidateStoreKey(string storeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);
    }

    private sealed class LibSecretNative : IDisposable
    {
        private const int _schemaAttributeString = 0;
        private readonly IntPtr _libSecret;
        private readonly IntPtr _libGlib;
        private readonly SchemaUnrefDelegate _schemaUnref;
        private readonly PasswordStoreDelegate _passwordStore;
        private readonly PasswordLookupDelegate _passwordLookup;
        private readonly PasswordClearDelegate _passwordClear;
        private readonly PasswordFreeDelegate _passwordFree;
        private readonly ErrorFreeDelegate _errorFree;
        private IntPtr _schema;

        private LibSecretNative(IntPtr libSecret, IntPtr libGlib)
        {
            _libSecret = libSecret;
            _libGlib = libGlib;
            var schemaNew = Load<SchemaNewDelegate>(libSecret, "secret_schema_new");
            _schemaUnref = Load<SchemaUnrefDelegate>(libSecret, "secret_schema_unref");
            _passwordStore = Load<PasswordStoreDelegate>(libSecret, "secret_password_store_sync");
            _passwordLookup = Load<PasswordLookupDelegate>(libSecret, "secret_password_lookup_sync");
            _passwordClear = Load<PasswordClearDelegate>(libSecret, "secret_password_clear_sync");
            _passwordFree = Load<PasswordFreeDelegate>(libSecret, "secret_password_free");
            _errorFree = Load<ErrorFreeDelegate>(libGlib, "g_error_free");

            using var schemaName = new NativeUtf8String("io.remoteflow.Secret".AsSpan());
            using var attributeName = new NativeUtf8String("store-key".AsSpan());
            _schema = schemaNew(
                schemaName.Pointer,
                0,
                attributeName.Pointer,
                _schemaAttributeString,
                IntPtr.Zero);
            if (_schema == IntPtr.Zero)
            {
                throw new CredentialProviderException("libsecret could not create the RemoteFlow schema.");
            }
        }

        internal static LibSecretNative? TryCreate()
        {
            if (!OperatingSystem.IsLinux() || !TryLoadLibrary(["libsecret-1.so.0", "libsecret-1.so"], out var libSecret))
            {
                return null;
            }

            if (!TryLoadLibrary(["libglib-2.0.so.0", "libglib-2.0.so"], out var libGlib))
            {
                NativeLibrary.Free(libSecret);
                return null;
            }

            try
            {
                return new LibSecretNative(libSecret, libGlib);
            }
            catch
            {
                NativeLibrary.Free(libGlib);
                NativeLibrary.Free(libSecret);
                return null;
            }
        }

        internal SecretHandle? Get(string storeKey)
        {
            using var attributeName = new NativeUtf8String("store-key".AsSpan());
            using var attributeValue = new NativeUtf8String(storeKey.AsSpan());
            var password = _passwordLookup(
                _schema,
                IntPtr.Zero,
                out var error,
                attributeName.Pointer,
                attributeValue.Pointer,
                IntPtr.Zero);
            ThrowAndFreeError(error, "read");
            if (password == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var bytes = CopyNullTerminatedUtf8(password);
                try
                {
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
                _passwordFree(password);
            }
        }

        internal void Set(string storeKey, ReadOnlySpan<char> secret, string displayName)
        {
            using var label = new NativeUtf8String(displayName.AsSpan());
            using var password = new NativeUtf8String(secret);
            using var attributeName = new NativeUtf8String("store-key".AsSpan());
            using var attributeValue = new NativeUtf8String(storeKey.AsSpan());
            var stored = _passwordStore(
                _schema,
                IntPtr.Zero,
                label.Pointer,
                password.Pointer,
                IntPtr.Zero,
                out var error,
                attributeName.Pointer,
                attributeValue.Pointer,
                IntPtr.Zero);
            ThrowAndFreeError(error, "store");
            if (stored == 0)
            {
                throw new CredentialProviderException("libsecret could not store the credential.");
            }
        }

        internal void Delete(string storeKey)
        {
            using var attributeName = new NativeUtf8String("store-key".AsSpan());
            using var attributeValue = new NativeUtf8String(storeKey.AsSpan());
            _ = _passwordClear(
                _schema,
                IntPtr.Zero,
                out var error,
                attributeName.Pointer,
                attributeValue.Pointer,
                IntPtr.Zero);
            ThrowAndFreeError(error, "delete");
        }

        public void Dispose()
        {
            var schema = Interlocked.Exchange(ref _schema, IntPtr.Zero);
            if (schema != IntPtr.Zero)
            {
                _schemaUnref(schema);
            }

            NativeLibrary.Free(_libGlib);
            NativeLibrary.Free(_libSecret);
        }

        private void ThrowAndFreeError(IntPtr error, string operation)
        {
            if (error == IntPtr.Zero)
            {
                return;
            }

            _errorFree(error);
            throw new CredentialProviderException($"libsecret could not {operation} the credential.");
        }

        private static T Load<T>(IntPtr library, string name)
            where T : Delegate
        {
            return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        }

        private static bool TryLoadLibrary(IEnumerable<string> names, out IntPtr handle)
        {
            foreach (var name in names)
            {
                if (NativeLibrary.TryLoad(name, out handle))
                {
                    return true;
                }
            }

            handle = IntPtr.Zero;
            return false;
        }

        private static byte[] CopyNullTerminatedUtf8(IntPtr pointer)
        {
            var length = 0;
            while (Marshal.ReadByte(pointer, length) != 0)
            {
                length++;
            }

            var bytes = new byte[length];
            if (length > 0)
            {
                Marshal.Copy(pointer, bytes, 0, length);
            }

            return bytes;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr SchemaNewDelegate(
            IntPtr name,
            int flags,
            IntPtr attributeName,
            int attributeType,
            IntPtr terminator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SchemaUnrefDelegate(IntPtr schema);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PasswordStoreDelegate(
            IntPtr schema,
            IntPtr collection,
            IntPtr label,
            IntPtr password,
            IntPtr cancellable,
            out IntPtr error,
            IntPtr attributeName,
            IntPtr attributeValue,
            IntPtr terminator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr PasswordLookupDelegate(
            IntPtr schema,
            IntPtr cancellable,
            out IntPtr error,
            IntPtr attributeName,
            IntPtr attributeValue,
            IntPtr terminator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int PasswordClearDelegate(
            IntPtr schema,
            IntPtr cancellable,
            out IntPtr error,
            IntPtr attributeName,
            IntPtr attributeValue,
            IntPtr terminator);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void PasswordFreeDelegate(IntPtr password);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ErrorFreeDelegate(IntPtr error);
    }

    private sealed class NativeUtf8String : IDisposable
    {
        private readonly int _byteCount;
        private IntPtr _pointer;

        internal NativeUtf8String(ReadOnlySpan<char> value)
        {
            var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
            _byteCount = bytes.Length;
            try
            {
                _ = Encoding.UTF8.GetBytes(value, bytes);
                _pointer = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, _pointer, bytes.Length);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        internal IntPtr Pointer => _pointer != IntPtr.Zero
            ? _pointer
            : throw new ObjectDisposedException(nameof(NativeUtf8String));

        public void Dispose()
        {
            var pointer = Interlocked.Exchange(ref _pointer, IntPtr.Zero);
            if (pointer == IntPtr.Zero)
            {
                return;
            }

            for (var index = 0; index < _byteCount; index++)
            {
                Marshal.WriteByte(pointer, index, 0);
            }

            Marshal.FreeHGlobal(pointer);
        }
    }
}
