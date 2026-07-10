using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class DesktopDiagnosticsService
{
    public DesktopDiagnosticsSnapshot BuildLocalSnapshot(DesktopDiagnosticsRequest request)
    {
        var resolvedConfig = DetectionConfigService.ResolveEffectiveConfigPath(request.ConfigPath);
        var inputReady = IsInputReady(request.InputDirectory);
        var configIssue = DetectionConfigService.ValidateConfigFile(resolvedConfig);
        var configReady = configIssue is null;

        return new DesktopDiagnosticsSnapshot(
            resolvedConfig,
            inputReady,
            configReady,
            string.IsNullOrWhiteSpace(request.InputDirectory)
                ? "未选择输入目录"
                : inputReady ? $"可用 · {request.InputDirectory}" : $"不存在或没有 CSV · {request.InputDirectory}",
            string.IsNullOrWhiteSpace(request.OutputDirectory)
                ? "将写入输入目录"
                : Directory.Exists(request.OutputDirectory) ? $"可用 · {request.OutputDirectory}" : request.WriteReport ? $"将自动创建 · {request.OutputDirectory}" : $"未使用 · {request.OutputDirectory}",
            configReady ? $"可用 · {resolvedConfig}" : configIssue ?? $"未找到 · {resolvedConfig}",
            "Native .NET 检测核心可用",
            inputReady && configReady ? "输入、配置和原生检测核心均已就绪" : "输入或配置需要处理");
    }

    public IReadOnlyList<string> ValidateRunInputs(DesktopDiagnosticsRequest request, bool prepareOutputDirectory)
    {
        var issues = new List<string>();
        var inputIssue = ValidateInputDirectory(request.InputDirectory);
        if (inputIssue is not null) issues.Add(inputIssue);
        var configIssue = DetectionConfigService.ValidateConfigFile(DetectionConfigService.ResolveEffectiveConfigPath(request.ConfigPath));
        if (configIssue is not null) issues.Add(configIssue);
        var outputIssue = ValidateOutputDirectory(request.OutputDirectory, request.WriteReport, prepareOutputDirectory);
        if (outputIssue is not null) issues.Add(outputIssue);
        return issues;
    }

    public static string BuildBlockStartAction(IReadOnlyList<string> issues) =>
        issues.Any(issue => issue.Contains("CSV", StringComparison.OrdinalIgnoreCase) || issue.Contains("输入", StringComparison.OrdinalIgnoreCase))
            ? "请选择包含 CSV 文件的输入目录。"
            : issues.Any(issue => issue.Contains("配置", StringComparison.OrdinalIgnoreCase))
                ? "请在设置中选择有效的阈值配置文件，或留空使用默认阈值配置。"
                : "请选择可写的报告目录，或留空写入输入目录。";

    public static bool IsInputReady(string inputDirectory) => ValidateInputDirectory(inputDirectory) is null;

    private static string? ValidateInputDirectory(string inputDirectory)
    {
        if (string.IsNullOrWhiteSpace(inputDirectory)) return "未选择输入目录";
        if (!Directory.Exists(inputDirectory)) return $"输入目录不存在: {inputDirectory}";
        try
        {
            return Directory.EnumerateFiles(inputDirectory, "*.csv", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive }).Any()
                ? null : $"输入目录中未找到 CSV 文件: {inputDirectory}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"无法扫描输入目录: {ex.Message}";
        }
    }

    private static string? ValidateOutputDirectory(string outputDirectory, bool writeReport, bool prepareOutputDirectory)
    {
        if (!writeReport || string.IsNullOrWhiteSpace(outputDirectory) || Directory.Exists(outputDirectory) || !prepareOutputDirectory) return null;
        try { Directory.CreateDirectory(outputDirectory); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return $"报告目录不可创建: {ex.Message}"; }
    }
}
