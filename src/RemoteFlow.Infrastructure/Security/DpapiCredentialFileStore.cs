using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RemoteFlow.Application.Abstractions;

namespace RemoteFlow.Infrastructure.Security;

internal sealed class DpapiCredentialFileStore
{
    private readonly string _directory;

    internal DpapiCredentialFileStore(IAppPaths appPaths)
    {
        _directory = Path.Combine(appPaths.ConfigDirectory, "credential-fallback");
    }

    internal SecretHandle? Get(string storeKey)
    {
        var path = GetPath(storeKey);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = File.ReadAllBytes(path);
        byte[]? plaintext = null;
        try
        {
            plaintext = Dpapi.Unprotect(protectedBytes);
            var chars = new char[Encoding.UTF8.GetCharCount(plaintext)];
            _ = Encoding.UTF8.GetChars(plaintext, chars);
            try
            {
                return new SecretHandle(chars);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan()));
            }
        }
        catch (CryptographicException exception)
        {
            throw new CredentialProviderException("The protected credential could not be opened.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            if (plaintext is not null)
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    internal void Set(string storeKey, ReadOnlySpan<char> secret)
    {
        _ = Directory.CreateDirectory(_directory);
        var bytes = new byte[Encoding.UTF8.GetByteCount(secret)];
        byte[]? protectedBytes = null;
        try
        {
            _ = Encoding.UTF8.GetBytes(secret, bytes);
            protectedBytes = Dpapi.Protect(bytes);
            File.WriteAllBytes(GetPath(storeKey), protectedBytes);
        }
        catch (CryptographicException exception)
        {
            throw new CredentialProviderException("The credential could not be protected with DPAPI.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
    }

    internal void Delete(string storeKey)
    {
        var path = GetPath(storeKey);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetPath(string storeKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(storeKey);
        try
        {
            return Path.Combine(_directory, $"{Convert.ToHexString(SHA256.HashData(keyBytes))}.bin");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static class Dpapi
    {
        private const uint _cryptProtectUiForbidden = 0x1;

        internal static byte[] Protect(byte[] plaintext)
        {
            return Transform(plaintext, protect: true);
        }

        internal static byte[] Unprotect(byte[] ciphertext)
        {
            return Transform(ciphertext, protect: false);
        }

        private static byte[] Transform(byte[] input, bool protect)
        {
            var inputPointer = Marshal.AllocHGlobal(Math.Max(input.Length, 1));
            var inputBlob = new DataBlob { Size = input.Length, Data = inputPointer };
            var outputBlob = default(DataBlob);
            try
            {
                if (input.Length > 0)
                {
                    Marshal.Copy(input, 0, inputPointer, input.Length);
                }

                var success = protect
                    ? NativeMethods.CryptProtectData(
                        ref inputBlob,
                        null,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        _cryptProtectUiForbidden,
                        out outputBlob)
                    : NativeMethods.CryptUnprotectData(
                        ref inputBlob,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        _cryptProtectUiForbidden,
                        out outputBlob);
                if (!success)
                {
                    throw new CryptographicException(
                        "Windows data protection failed.",
                        new Win32Exception(Marshal.GetLastPInvokeError()));
                }

                var output = new byte[outputBlob.Size];
                if (output.Length > 0)
                {
                    Marshal.Copy(outputBlob.Data, output, 0, output.Length);
                }

                return output;
            }
            finally
            {
                for (var index = 0; index < input.Length; index++)
                {
                    Marshal.WriteByte(inputPointer, index, 0);
                }

                Marshal.FreeHGlobal(inputPointer);
                if (outputBlob.Data != IntPtr.Zero)
                {
                    for (var index = 0; index < outputBlob.Size; index++)
                    {
                        Marshal.WriteByte(outputBlob.Data, index, 0);
                    }

                    _ = NativeMethods.LocalFree(outputBlob.Data);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            internal int Size;
            internal IntPtr Data;
        }

        private static class NativeMethods
        {
#pragma warning disable SYSLIB1054
            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptProtectData(
                ref DataBlob dataIn,
                string? description,
                IntPtr optionalEntropy,
                IntPtr reserved,
                IntPtr promptStruct,
                uint flags,
                out DataBlob dataOut);

            [DllImport("crypt32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CryptUnprotectData(
                ref DataBlob dataIn,
                IntPtr description,
                IntPtr optionalEntropy,
                IntPtr reserved,
                IntPtr promptStruct,
                uint flags,
                out DataBlob dataOut);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr LocalFree(IntPtr memory);
#pragma warning restore SYSLIB1054
        }
    }
}
