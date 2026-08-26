using System.Runtime.InteropServices;

namespace RemoteFlow.Infrastructure.Pty;

/// <summary>
/// Turns newline translation back on for a freshly spawned Linux PTY.
/// </summary>
/// <remarks>
/// <para>
/// Porta.Pty 1.0.7 builds the Linux terminal with <c>TermOutputFlag.NONE</c>, so <c>OPOST</c> and
/// <c>ONLCR</c> are clear and the kernel never turns a child process's <c>\n</c> into <c>\r\n</c>. A
/// conformant terminal answers a bare line feed by moving down one row and staying in the same column, so
/// every line of output started where the previous one ended: the staircase that made <c>ls</c> unreadable.
/// It also desynchronises readline, which counts columns itself and then redraws the edited line over the
/// wrong ones — which is why typing or pasting smeared the same character across a row.
/// </para>
/// <para>
/// The library's macOS provider passes <c>OPOST | ONLCR</c> and Windows uses ConPTY, which renders the
/// screen itself. Linux was the only platform affected, which is why PowerShell on Windows looked right.
/// </para>
/// <para>
/// The flags are restored on the master file descriptor, which for a pseudoterminal shares one termios with
/// the slave the child holds. This is best effort: a terminal that staircases is worth more than a terminal
/// that refuses to start, so a failure here is reported rather than thrown.
/// </para>
/// </remarks>
internal static class PtyOutputPostProcessing
{
    /// <summary>Linux <c>tcflag_t</c> is 4 bytes and <c>c_oflag</c> is the second field.</summary>
    private const int _outputFlagOffset = 4;

    private const int _opost = 0x1;
    private const int _onlcr = 0x4;
    private const int _setNow = 0;

    /// <summary>struct termios is 60 bytes on Linux; the surplus keeps a libc revision from writing past it.</summary>
    private const int _termiosBufferSize = 256;

    /// <summary>
    /// Adds <c>OPOST</c> and <c>ONLCR</c> to the terminal behind <paramref name="stream" />.
    /// </summary>
    /// <returns>
    /// <see langword="true" /> when the terminal now post-processes output, including when it already did.
    /// <see langword="false" /> when the platform is not Linux or the terminal could not be reconfigured.
    /// </returns>
    internal static bool TryEnable(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!OperatingSystem.IsLinux() || stream is not FileStream file)
        {
            return false;
        }

        var termios = Marshal.AllocHGlobal(_termiosBufferSize);
        try
        {
            var descriptor = (int)file.SafeFileHandle.DangerousGetHandle();
            if (NativeMethods.tcgetattr(descriptor, termios) != 0)
            {
                return false;
            }

            var outputFlags = Marshal.ReadInt32(termios, _outputFlagOffset);
            var restored = outputFlags | _opost | _onlcr;
            if (restored == outputFlags)
            {
                return true;
            }

            Marshal.WriteInt32(termios, _outputFlagOffset, restored);
            return NativeMethods.tcsetattr(descriptor, _setNow, termios) == 0;
        }
        catch (ObjectDisposedException)
        {
            // The child can exit, and the streams close, between the spawn and this call.
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(termios);
        }
    }

    private static class NativeMethods
    {
        // DllImport rather than LibraryImport: the generated marshalling code needs AllowUnsafeBlocks,
        // which this repository does not enable. Both arguments are already blittable.
        [DllImport("libc", SetLastError = true)]
        internal static extern int tcgetattr(int fileDescriptor, IntPtr termios);

        [DllImport("libc", SetLastError = true)]
        internal static extern int tcsetattr(int fileDescriptor, int optionalActions, IntPtr termios);
    }
}
