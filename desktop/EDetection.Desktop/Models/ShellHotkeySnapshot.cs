namespace EDetection.Desktop.Models;

public sealed record ShellHotkeySnapshot(
    bool IsEnabled,
    bool RestoreRegistered,
    string RestoreGesture)
{
    public static ShellHotkeySnapshot Disabled { get; } = new(
        IsEnabled: false,
        RestoreRegistered: false,
        RestoreGesture: "Ctrl+Alt+Shift+E");

    public bool IsFullyRegistered => IsEnabled && RestoreRegistered;

    public string StatusText
    {
        get
        {
            if (!IsEnabled)
            {
                return "全局热键未启用";
            }

            if (IsFullyRegistered)
            {
                return $"全局热键已启用 · {RestoreGesture} 恢复工作台";
            }

            if (!RestoreRegistered)
            {
                return "全局热键注册失败 · 组合键可能被占用";
            }

            return $"全局热键状态: {RestoreGesture}";
        }
    }
}
