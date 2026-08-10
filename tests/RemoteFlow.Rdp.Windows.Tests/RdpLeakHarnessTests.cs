using System.Diagnostics;
using System.Runtime.InteropServices;
using RemoteFlow.Rdp.Windows.Hosting;
using RemoteFlow.Rdp.Windows.Interop;
using Xunit;

namespace RemoteFlow.Rdp.Windows.Tests;

public sealed class RdpLeakHarnessTests
{
    private const int _cycles = 20;

    [Fact]
    public async Task TwentyControlLifetimesStayInsideMeasuredNativeResourceBudget()
    {
        var measurement = await RunOnStaThreadAsync(TestContext.Current.CancellationToken);

        // Baseline recorded on Windows 11 24H2 after one warm-up activation (2026-08-10): the 20-cycle
        // run retained 0 GDI handles, 0 USER handles, 0 live controls, and 2.9 MiB private memory. The
        // budgets allow normal mstscax/OS allocator variation while still catching an instance-per-cycle leak.
        Assert.True(measurement.GdiDelta <= 8, $"GDI handle delta was {measurement.GdiDelta}.");
        Assert.True(measurement.UserDelta <= 8, $"USER handle delta was {measurement.UserDelta}.");
        Assert.True(
            measurement.PrivateBytesDelta <= 16L * 1024 * 1024,
            $"Private-memory delta was {measurement.PrivateBytesDelta / 1048576d:F1} MiB.");
        Assert.Equal(0, measurement.LiveControls);

        Console.WriteLine(
            $"RDP leak harness ({_cycles} cycles): GDI {measurement.GdiDelta:+#;-#;0}, " +
            $"USER {measurement.UserDelta:+#;-#;0}, private {measurement.PrivateBytesDelta / 1048576d:F1} MiB, " +
            $"live controls {measurement.LiveControls}.");
    }

    private static Task<LeakMeasurement> RunOnStaThreadAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<LeakMeasurement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Marshal.ThrowExceptionForHR(LeakNativeMethods.OleInitialize(IntPtr.Zero));
                try
                {
                    RunCycle();
                    ForceCollection();
                    var before = CaptureResources();
                    for (var index = 0; index < _cycles; index++)
                    {
                        RunCycle();
                    }
                    ForceCollection();
                    var after = CaptureResources();
                    _ = completion.TrySetResult(new LeakMeasurement(
                        after.GdiHandles - before.GdiHandles,
                        after.UserHandles - before.UserHandles,
                        after.PrivateBytes - before.PrivateBytes,
                        WindowsNativeRdpControl.LiveInstanceCount));
                }
                finally
                {
                    LeakNativeMethods.OleUninitialize();
                }
            }
            catch (Exception exception)
            {
                _ = completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "RemoteFlow RDP leak harness",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(cancellationToken);
    }

    private static void RunCycle()
    {
        var control = WindowsNativeRdpControlFactory.Instance.Create(CreateSettings(), CancellationToken.None);
        var container = new OleRdpControlContainer(control);
        try
        {
            container.Create(800, 600);
        }
        finally
        {
            container.Dispose();
            control.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (container.Handle != IntPtr.Zero)
        {
            throw new InvalidOperationException("The RDP container left its HWND alive.");
        }
        if (WindowsNativeRdpControl.LiveInstanceCount != 0)
        {
            throw new InvalidOperationException("The RDP control RCW remained live after a harness cycle.");
        }
    }

    private static RdpControlSettings CreateSettings()
    {
        return new RdpControlSettings(
            "127.0.0.1",
            3389,
            null,
            null,
            800,
            600,
            32,
            new RdpControlAdvancedSettings(
                RedirectClipboard: false,
                RedirectDrives: false,
                AuthenticationLevel: 2,
                EnableCredSspSupport: true,
                SmartSizing: false,
                KeyboardHookMode: RdpKeyboardHookMode.OnRemoteComputer),
            DesktopScaleFactor: 100,
            DeviceScaleFactor: 100,
            new IgnoredExternalRdpDisplayOptions(false, false));
    }

    private static ResourceSnapshot CaptureResources()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ResourceSnapshot(
            LeakNativeMethods.GetGuiResources(process.Handle, 0),
            LeakNativeMethods.GetGuiResources(process.Handle, 1),
            process.PrivateMemorySize64);
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed record ResourceSnapshot(int GdiHandles, int UserHandles, long PrivateBytes);

    private sealed record LeakMeasurement(int GdiDelta, int UserDelta, long PrivateBytesDelta, int LiveControls);
}

internal static class LeakNativeMethods
{
#pragma warning disable SYSLIB1054 // Test-only measurement P/Invokes avoid enabling unsafe source generation.
    [DllImport("ole32.dll")]
    internal static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    internal static extern void OleUninitialize();

    [DllImport("user32.dll")]
    internal static extern int GetGuiResources(IntPtr process, uint flags);
#pragma warning restore SYSLIB1054
}
