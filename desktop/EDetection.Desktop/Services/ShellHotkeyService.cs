using System.Runtime.InteropServices;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class ShellHotkeyService
{
    public const uint WmHotkey = 0x0312;

    private const int RestoreWorkbenchId = 0x4544;
    private const uint RestoreVirtualKey = 0x45;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint Modifiers = ModAlt | ModControl | ModShift | ModNoRepeat;

    public ShellHotkeySnapshot Register(nint hwnd, bool enabled)
    {
        Unregister(hwnd);
        if (hwnd == 0 || !enabled)
        {
            return ShellHotkeySnapshot.Disabled;
        }

        var restoreRegistered = RegisterHotKey(hwnd, RestoreWorkbenchId, Modifiers, RestoreVirtualKey);
        return new ShellHotkeySnapshot(
            IsEnabled: true,
            restoreRegistered,
            ShellHotkeySnapshot.Disabled.RestoreGesture);
    }

    public void Unregister(nint hwnd)
    {
        if (hwnd == 0)
        {
            return;
        }

        UnregisterHotKey(hwnd, RestoreWorkbenchId);
    }

    public ShellHotkeyAction ResolveAction(nuint hotkeyId) =>
        unchecked((int)hotkeyId) switch
        {
            RestoreWorkbenchId => ShellHotkeyAction.RestoreWorkbench,
            _ => ShellHotkeyAction.None,
        };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
