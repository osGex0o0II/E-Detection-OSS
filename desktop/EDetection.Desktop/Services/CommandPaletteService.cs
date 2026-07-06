using System.Windows.Input;
using EDetection.Desktop.Models;
using EDetection.Desktop.ViewModels;

namespace EDetection.Desktop.Services;

public sealed class CommandPaletteService
{
    public IReadOnlyList<CommandPaletteAction> Build(CommandPaletteContext context)
    {
        var actions = new List<CommandPaletteAction>
        {
            new(
                "选择输入目录",
                "选择包含 CSV 文件的根目录",
                "Ctrl+O",
                "\uE8B7",
                "运行",
                context.BrowseInputDirectoryAsync),
            FromCommand(
                "开始检测",
                "运行当前输入目录的异常检测",
                "F5",
                "\uE768",
                "运行",
                context.StartCommand,
                context.StartCommandParameter),
            FromCommand(
                "取消检测",
                "停止当前正在运行的检测",
                "Esc",
                "\uE711",
                "运行",
                context.CancelCommand,
                context.CancelCommandParameter),
            FromCommand(
                "检查状态",
                "检查输入、阈值设置与检测组件",
                "F6",
                "\uE895",
                "检查",
                context.DiagnosticsCommand,
                context.DiagnosticsCommandParameter),
            FromCommand(
                "复制状态详情",
                "复制当前输入、阈值设置与组件状态摘要",
                "",
                "\uE8C8",
                "检查",
                context.ViewModel?.CopyDiagnosticsCommand,
                null),
            FromCommand(
                "刷新应用状态",
                "重新检查通知、启动项、包完整性和全局热键状态",
                "",
                "\uE72C",
                "检查",
                context.ViewModel?.RefreshDesktopHealthCommand,
                null),
            new(
                "打开设置",
                "调整路径、报告、运行记录、外观与通知",
                "Ctrl+S",
                "\uE713",
                "设置",
                context.OpenSettingsAsync),
            new(
                "打开阈值设置",
                "调整电压、电流、温度、冻结等检测阈值",
                "",
                "\uE9D2",
                "设置",
                context.OpenThresholdSettingsAsync),
            new(
                "打开检测规则",
                "启用或关闭电流、功率因数和详细输出规则",
                "",
                "\uE9D5",
                "设置",
                context.OpenDetectionRulesAsync),
            FromCommand(
                "打开最新报告",
                "打开当前检测或选中历史报告的 Excel 文件",
                "",
                "\uE8A5",
                "报告",
                context.ViewModel?.OpenCurrentReportCommand,
                null),
            FromCommand(
                "打开报告目录",
                "在资源管理器中打开当前报告所在目录",
                "",
                "\uE8B7",
                "报告",
                context.ViewModel?.OpenCurrentReportFolderCommand,
                null),
            FromCommand(
                "复制报告路径",
                "复制当前检测或选中历史报告的路径",
                "",
                "\uE8C8",
                "报告",
                context.ViewModel?.CopyCurrentReportPathCommand,
                null),
            new(
                "选择阈值配置文件",
                "选择检测阈值和规则配置文件",
                "Ctrl+1",
                "\uE8A5",
                "设置",
                context.BrowseConfigPathAsync),
            new(
                "选择检测组件",
                "选择检测组件运行程序",
                "Ctrl+2",
                "\uE756",
                "设置",
                context.BrowsePythonExecutableAsync),
            new(
                "关于 E-Detection",
                "查看版本、运行时与架构",
                "Ctrl+I",
                "\uE946",
                "窗口",
                context.OpenAboutAsync),
        };

        if (context.ViewModel is { } viewModel)
        {
            foreach (var report in viewModel.ReportHistory.RecentReports.Take(5).ToList())
            {
                actions.Add(BuildRecentReportAction(viewModel, report));
            }
        }

        return actions;
    }

    public IReadOnlyList<CommandPaletteAction> Filter(
        IEnumerable<CommandPaletteAction> actions,
        string query)
    {
        var normalizedQuery = query.Trim();
        return actions
            .Select(action => new
            {
                Action = action,
                Score = action.MatchScore(normalizedQuery),
            })
            .Where(item => item.Score >= 0)
            .OrderByDescending(item => item.Action.IsEnabled)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => CategorySortIndex(item.Action.Category))
            .ThenBy(item => item.Action.Title)
            .Select(item => item.Action)
            .ToList();
    }

    private static CommandPaletteAction BuildRecentReportAction(
        MainViewModel viewModel,
        RecentReport report) =>
        new(
            $"打开最近报告: {report.FileName}",
            report.Summary,
            "最近",
            "\uE8A5",
            "历史",
            () =>
            {
                viewModel.ReportHistory.SelectedReport = report;
                return ExecuteCommandAsync(viewModel.OpenSelectedReportCommand, null);
            },
            () => File.Exists(report.Path));

    private static CommandPaletteAction FromCommand(
        string title,
        string description,
        string shortcut,
        string glyph,
        string category,
        ICommand? command,
        object? parameter) =>
        new(
            title,
            description,
            shortcut,
            glyph,
            category,
            () => ExecuteCommandAsync(command, parameter),
            () => command?.CanExecute(parameter) == true);

    private static Task ExecuteCommandAsync(ICommand? command, object? parameter)
    {
        if (command?.CanExecute(parameter) == true)
        {
            command.Execute(parameter);
        }

        return Task.CompletedTask;
    }

    private static int CategorySortIndex(string category) => category switch
    {
        "运行" => 0,
        "检查" => 1,
        "报告" => 2,
        "历史" => 3,
        "窗口" => 4,
        "设置" => 5,
        _ => 99,
    };
}

public sealed record CommandPaletteContext(
    MainViewModel? ViewModel,
    ICommand? StartCommand,
    object? StartCommandParameter,
    ICommand? CancelCommand,
    object? CancelCommandParameter,
    ICommand? DiagnosticsCommand,
    object? DiagnosticsCommandParameter,
    Func<Task> BrowseInputDirectoryAsync,
    Func<Task> BrowseConfigPathAsync,
    Func<Task> BrowsePythonExecutableAsync,
    Func<Task> OpenSettingsAsync,
    Func<Task> OpenThresholdSettingsAsync,
    Func<Task> OpenDetectionRulesAsync,
    Func<Task> OpenAboutAsync);
