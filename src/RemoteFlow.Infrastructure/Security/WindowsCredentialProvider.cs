using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Security;

public sealed class WindowsCredentialProvider : ICredentialProvider
{
    public const int MaximumCredentialBlobBytes = 2560;

    private const int _errorNotFound = 1168;
    private const int _errorNoSuchLogonSession = 1312;
    private const int _errorNotSupported = 50;
    private const int _errorCallNotImplemented = 120;

    private readonly DpapiCredentialFileStore _fallback;

    public WindowsCredentialProvider(IAppPaths appPaths)
    {
        ArgumentNullException.ThrowIfNull(appPaths);
        _fallback = new DpapiCredentialFileStore(appPaths);
    }

    public string Name => "windows-credman";

    public bool IsAvailable => OperatingSystem.IsWindows();

    public Task<SecretHandle?> GetAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        if (NativeMethods.CredRead(storeKey, NativeMethods.CredentialTypeGeneric, 0, out var credentialPointer))
        {
            try
            {
                var credential = Marshal.PtrToStructure<NativeMethods.Credential>(credentialPointer);
                if (credential.CredentialBlobSize == 0)
                {
                    return Task.FromResult<SecretHandle?>(new SecretHandle([]));
                }

                var bytes = new byte[credential.CredentialBlobSize];
                try
                {
                    Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                    var chars = new char[Encoding.UTF8.GetCharCount(bytes)];
                    _ = Encoding.UTF8.GetChars(bytes, chars);
                    try
                    {
                        return Task.FromResult<SecretHandle?>(new SecretHandle(chars));
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
                NativeMethods.CredFree(credentialPointer);
            }
        }

        var error = Marshal.GetLastPInvokeError();
        return error == _errorNotFound || IsCredentialManagerUnavailable(error)
            ? Task.FromResult(_fallback.Get(storeKey))
            : throw CreateNativeException("read", error);
    }

    public Task SetAsync(
        string storeKey,
        ReadOnlyMemory<char> secret,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        var byteCount = Encoding.UTF8.GetByteCount(secret.Span);
        if (byteCount > MaximumCredentialBlobBytes)
        {
            throw new CredentialTooLargeException(byteCount, MaximumCredentialBlobBytes);
        }

        var bytes = new byte[byteCount];
        var blobPointer = IntPtr.Zero;
        try
        {
            _ = Encoding.UTF8.GetBytes(secret.Span, bytes);
            if (bytes.Length > 0)
            {
                blobPointer = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, blobPointer, bytes.Length);
            }

            var credential = new NativeMethods.Credential
            {
                Type = NativeMethods.CredentialTypeGeneric,
                TargetName = storeKey,
                CredentialBlobSize = checked((uint)bytes.Length),
                CredentialBlob = blobPointer,
                Persist = NativeMethods.CredentialPersistLocalMachine,
                UserName = displayName,
            };

            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                var error = Marshal.GetLastPInvokeError();
                if (IsCredentialManagerUnavailable(error))
                {
                    _fallback.Set(storeKey, secret.Span);
                    return Task.CompletedTask;
                }

                throw CreateNativeException("write", error);
            }

            _fallback.Delete(storeKey);
            return Task.CompletedTask;
        }
        finally
        {
            if (blobPointer != IntPtr.Zero)
            {
                for (var index = 0; index < bytes.Length; index++)
                {
                    Marshal.WriteByte(blobPointer, index, 0);
                }

                Marshal.FreeCoTaskMem(blobPointer);
            }

            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public Task DeleteAsync(string storeKey, CancellationToken cancellationToken = default)
    {
        ValidateStoreKey(storeKey);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();

        if (!NativeMethods.CredDelete(storeKey, NativeMethods.CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != _errorNotFound && !IsCredentialManagerUnavailable(error))
            {
                throw CreateNativeException("delete", error);
            }
        }

        _fallback.Delete(storeKey);
        return Task.CompletedTask;
    }

    private static void ValidateStoreKey(string storeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeKey);
        if (storeKey.Length > 512)
        {
            throw new ArgumentException("Credential store keys cannot exceed 512 characters.", nameof(storeKey));
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows Credential Manager is only available on Windows.");
        }
    }

    private static bool IsCredentialManagerUnavailable(int error)
    {
        return error is _errorNoSuchLogonSession or _errorNotSupported or _errorCallNotImplemented;
    }

    private static CredentialProviderException CreateNativeException(string operation, int error)
    {
        return new CredentialProviderException(
            $"Windows Credential Manager could not {operation} the credential.",
            new Win32Exception(error));
    }

    private static class NativeMethods
    {
        internal const uint CredentialTypeGeneric = 1;
        internal const uint CredentialPersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Credential
        {
            internal uint Flags;
            internal uint Type;
            [MarshalAs(UnmanagedType.LPWStr)]
            internal string? TargetName;
            [MarshalAs(UnmanagedType.LPWStr)]
            internal string? Comment;
            internal long LastWritten;
            internal uint CredentialBlobSize;
            internal IntPtr CredentialBlob;
            internal uint Persist;
            internal uint AttributeCount;
            internal IntPtr Attributes;
            [MarshalAs(UnmanagedType.LPWStr)]
            internal string? TargetAlias;
            [MarshalAs(UnmanagedType.LPWStr)]
            internal string? UserName;
        }

#pragma warning disable SYSLIB1054
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        internal static extern void CredFree(IntPtr buffer);
#pragma warning restore SYSLIB1054
    }
}
