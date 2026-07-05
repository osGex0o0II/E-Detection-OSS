using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace EDetection.Desktop.Services;

public sealed class ShellResourceService
{
    private int _releaseQueued;

    public void ReleaseAfterHideToTray()
    {
        if (Interlocked.Exchange(ref _releaseQueued, 1) == 1)
        {
            return;
        }

        _ = Task.Run(ReleaseResources);
    }

    private void ReleaseResources()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();

            using var process = Process.GetCurrentProcess();
            SetProcessWorkingSetSize(process.Handle, -1, -1);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ShellResourceService] Failed to release hidden shell resources: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _releaseQueued, 0);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(
        nint process,
        nint minimumWorkingSetSize,
        nint maximumWorkingSetSize);
}
