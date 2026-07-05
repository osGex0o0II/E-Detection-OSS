using System.Runtime.InteropServices;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class TaskbarProgressService
{
    private static readonly Guid TaskbarListClsid = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    private readonly ITaskbarList3? _taskbar;

    public TaskbarProgressService()
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(6, 1))
            {
                var taskbarType = Type.GetTypeFromCLSID(TaskbarListClsid);
                if (taskbarType is not null && Activator.CreateInstance(taskbarType) is ITaskbarList3 taskbar)
                {
                    _taskbar = taskbar;
                    _taskbar.HrInit();
                }
            }
        }
        catch
        {
            _taskbar = null;
        }
    }

    public void Update(nint hwnd, TaskbarProgressKind kind, double percent)
    {
        if (hwnd == 0 || _taskbar is null)
        {
            return;
        }

        var flags = kind switch
        {
            TaskbarProgressKind.Indeterminate => TaskbarProgressFlags.Indeterminate,
            TaskbarProgressKind.Normal => TaskbarProgressFlags.Normal,
            TaskbarProgressKind.Paused => TaskbarProgressFlags.Paused,
            TaskbarProgressKind.Error => TaskbarProgressFlags.Error,
            _ => TaskbarProgressFlags.NoProgress,
        };

        try
        {
            _taskbar.SetProgressState(hwnd, flags);
            if (flags is TaskbarProgressFlags.Normal
                or TaskbarProgressFlags.Paused
                or TaskbarProgressFlags.Error)
            {
                var value = (ulong)Math.Clamp(percent, 0, 100);
                _taskbar.SetProgressValue(hwnd, value, 100);
            }
        }
        catch
        {
        }
    }

    public void Update(nint hwnd, ShellStatusSnapshot status) =>
        Update(hwnd, status.TaskbarProgressKind, status.TaskbarProgressPercent);

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEA84")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(nint hwnd);
        void DeleteTab(nint hwnd);
        void ActivateTab(nint hwnd);
        void SetActiveAlt(nint hwnd);
        void MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(nint hwnd, ulong completed, ulong total);
        void SetProgressState(nint hwnd, TaskbarProgressFlags flags);
    }

    private enum TaskbarProgressFlags
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }
}
