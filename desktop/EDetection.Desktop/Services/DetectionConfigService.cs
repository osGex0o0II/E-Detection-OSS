using System.Text.Json;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class DetectionConfigService
{
    public static readonly string DefaultUserConfigPath = Path.Combine(
        SettingsService.SettingsDirectory,
        "detection-config.json");

    private const string VoltageMinThresholdKey = "V_MIN_THRESHOLD";
    private const string VoltageMaxThresholdKey = "V_MAX_THRESHOLD";
    private const string CurrentMaxThresholdKey = "I_MAX_THRESHOLD";
    private const string CurrentUnbalanceMaxThresholdKey = "I_UNBALANCE_MAX_THRESHOLD";
    private const string ActivePowerMinThresholdKey = "P_ACTIVE_MIN_THRESHOLD";
    private const string PowerFactorMinThresholdKey = "PF_MIN_THRESHOLD";
    private const string TemperatureMinThresholdKey = "T_MIN_THRESHOLD";
    private const string TemperatureMaxThresholdKey = "T_MAX_THRESHOLD";
    private const string CurrentActiveMinThresholdKey = "I_MIN_ACTIVE_THRESHOLD";
    private const string FreezeCountThresholdKey = "FREEZE_COUNT_THRESHOLD";
    private const string FreezeStdThresholdKey = "FREEZE_STD_THRESHOLD";
    private const string VoltageImbalanceThresholdKey = "V_IMBALANCE_THRESHOLD";
    private const string CurrentOverloadKey = "current_overload";
    private const string CurrentUnbalanceKey = "current_unbalance";
    private const string LegacyCurrentKey = "current";
    private const string PowerFactorKey = "power_factor";
    private const string DetailOutputKey = "detail_output";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public DetectionConfigSettings CreateDefault() => new();

    public string EnsureUserConfig(string? preferredPath = null)
    {
        try
        {
            var targetPath = ResolveEffectiveConfigPath(preferredPath);
            if (File.Exists(targetPath))
            {
                return targetPath;
            }

            var bundledConfigPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            var initialConfig = File.Exists(bundledConfigPath)
                ? Load(bundledConfigPath)
                : CreateDefault();
            Save(targetPath, initialConfig);
            return targetPath;
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return DefaultUserConfigPath;
        }
    }

    public static string ResolveEffectiveConfigPath(string? preferredPath)
    {
        if (string.IsNullOrWhiteSpace(preferredPath))
        {
            return DefaultUserConfigPath;
        }

        try
        {
            return IsBundledDefaultConfigPath(preferredPath)
                ? DefaultUserConfigPath
                : ResolveConfigPath(preferredPath);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return DefaultUserConfigPath;
        }
    }

    public DetectionConfigSettings Load(string path)
    {
        var settings = CreateDefault();
        try
        {
            var resolvedPath = ResolveConfigPath(path);
            if (!File.Exists(resolvedPath))
            {
                return settings;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(resolvedPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return settings;
            }

            var root = document.RootElement;
            settings.VoltageMinThreshold = GetDouble(root, VoltageMinThresholdKey, settings.VoltageMinThreshold);
            settings.VoltageMaxThreshold = GetDouble(root, VoltageMaxThresholdKey, settings.VoltageMaxThreshold);
            settings.CurrentMaxThreshold = GetDouble(root, CurrentMaxThresholdKey, settings.CurrentMaxThreshold);
            settings.CurrentUnbalanceMaxThreshold = GetDouble(root, CurrentUnbalanceMaxThresholdKey, settings.CurrentUnbalanceMaxThreshold);
            settings.ActivePowerMinThreshold = GetDouble(root, ActivePowerMinThresholdKey, settings.ActivePowerMinThreshold);
            settings.PowerFactorMinThreshold = GetDouble(root, PowerFactorMinThresholdKey, settings.PowerFactorMinThreshold);
            settings.TemperatureMinThreshold = GetDouble(root, TemperatureMinThresholdKey, settings.TemperatureMinThreshold);
            settings.TemperatureMaxThreshold = GetDouble(root, TemperatureMaxThresholdKey, settings.TemperatureMaxThreshold);
            settings.CurrentActiveMinThreshold = GetDouble(root, CurrentActiveMinThresholdKey, settings.CurrentActiveMinThreshold);
            settings.FreezeCountThreshold = GetDouble(root, FreezeCountThresholdKey, settings.FreezeCountThreshold);
            settings.FreezeStdThreshold = GetDouble(root, FreezeStdThresholdKey, settings.FreezeStdThreshold);
            settings.VoltageImbalanceThreshold = GetDouble(root, VoltageImbalanceThresholdKey, settings.VoltageImbalanceThreshold);
            settings.CurrentOverloadEnabled = GetBool(root, CurrentOverloadKey, settings.CurrentOverloadEnabled);
            settings.CurrentUnbalanceEnabled = GetBool(root, CurrentUnbalanceKey, settings.CurrentUnbalanceEnabled);
            settings.PowerFactorEnabled = GetBool(root, PowerFactorKey, settings.PowerFactorEnabled);
            settings.DetailOutputEnabled = GetBool(root, DetailOutputKey, settings.DetailOutputEnabled);

            if (root.TryGetProperty(LegacyCurrentKey, out var legacyCurrent)
                && legacyCurrent.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                settings.CurrentOverloadEnabled = legacyCurrent.GetBoolean();
                settings.CurrentUnbalanceEnabled = legacyCurrent.GetBoolean();
            }
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or JsonException
                                   or ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return CreateDefault();
        }

        return Normalize(settings);
    }

    public static string? ValidateConfigFile(string path)
    {
        string resolvedPath;
        try
        {
            resolvedPath = ResolveConfigPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return $"阈值配置文件路径无效: {ex.Message}";
        }

        if (!File.Exists(resolvedPath))
        {
            return $"阈值配置文件不存在: {resolvedPath}";
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resolvedPath));
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? null
                : $"阈值配置文件格式错误: {resolvedPath}";
        }
        catch (JsonException ex)
        {
            return $"阈值配置文件无法解析: {resolvedPath} · {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return $"阈值配置文件不可读取: {ex.Message}";
        }
    }

    public void Save(string path, DetectionConfigSettings settings)
    {
        var resolvedPath = ResolveConfigPath(path);
        var normalized = Normalize(settings);
        var config = new Dictionary<string, object>
        {
            [VoltageMinThresholdKey] = normalized.VoltageMinThreshold,
            [VoltageMaxThresholdKey] = normalized.VoltageMaxThreshold,
            [CurrentMaxThresholdKey] = normalized.CurrentMaxThreshold,
            [CurrentUnbalanceMaxThresholdKey] = normalized.CurrentUnbalanceMaxThreshold,
            [ActivePowerMinThresholdKey] = normalized.ActivePowerMinThreshold,
            [PowerFactorMinThresholdKey] = normalized.PowerFactorMinThreshold,
            [TemperatureMinThresholdKey] = normalized.TemperatureMinThreshold,
            [TemperatureMaxThresholdKey] = normalized.TemperatureMaxThreshold,
            [CurrentActiveMinThresholdKey] = normalized.CurrentActiveMinThreshold,
            [FreezeCountThresholdKey] = (int)Math.Round(normalized.FreezeCountThreshold),
            [FreezeStdThresholdKey] = normalized.FreezeStdThreshold,
            [VoltageImbalanceThresholdKey] = normalized.VoltageImbalanceThreshold,
            [CurrentOverloadKey] = normalized.CurrentOverloadEnabled,
            [CurrentUnbalanceKey] = normalized.CurrentUnbalanceEnabled,
            [PowerFactorKey] = normalized.PowerFactorEnabled,
            [DetailOutputKey] = normalized.DetailOutputEnabled,
        };

        var directory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            resolvedPath,
            JsonSerializer.Serialize(config, JsonOptions) + Environment.NewLine);
    }

    public static string ResolveConfigPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DefaultUserConfigPath;
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }

    public static bool IsBundledDefaultConfigPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        try
        {
            var resolvedPath = Path.GetFullPath(ResolveConfigPath(path));
            var bundledConfigPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "config.json"));
            return string.Equals(
                resolvedPath,
                bundledConfigPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }
    }

    private static DetectionConfigSettings Normalize(DetectionConfigSettings settings) => new()
    {
        VoltageMinThreshold = ClampFinite(settings.VoltageMinThreshold, 0, 1000, 353.0),
        VoltageMaxThreshold = ClampFinite(settings.VoltageMaxThreshold, 0, 1000, 430.0),
        CurrentMaxThreshold = ClampFinite(settings.CurrentMaxThreshold, 0, 100000, 1000.0),
        CurrentUnbalanceMaxThreshold = ClampFinite(settings.CurrentUnbalanceMaxThreshold, 0, 1, 0.15),
        ActivePowerMinThreshold = FiniteOrDefault(settings.ActivePowerMinThreshold, 0),
        PowerFactorMinThreshold = ClampFinite(settings.PowerFactorMinThreshold, 0, 1, 0.9),
        TemperatureMinThreshold = ClampFinite(settings.TemperatureMinThreshold, -50, 200, 0),
        TemperatureMaxThreshold = ClampFinite(settings.TemperatureMaxThreshold, -50, 200, 70),
        CurrentActiveMinThreshold = ClampFinite(settings.CurrentActiveMinThreshold, 0.001, 100000, 1),
        FreezeCountThreshold = ClampFinite(Math.Round(settings.FreezeCountThreshold), 1, 1000, 3),
        FreezeStdThreshold = ClampFinite(settings.FreezeStdThreshold, 0, 1, 0.01),
        VoltageImbalanceThreshold = ClampFinite(settings.VoltageImbalanceThreshold, 0.001, 1, 0.02),
        CurrentOverloadEnabled = settings.CurrentOverloadEnabled,
        CurrentUnbalanceEnabled = settings.CurrentUnbalanceEnabled,
        PowerFactorEnabled = settings.PowerFactorEnabled,
        DetailOutputEnabled = settings.DetailOutputEnabled,
    };

    private static double GetDouble(JsonElement root, string key, double fallback)
    {
        if (!root.TryGetProperty(key, out var value))
        {
            return fallback;
        }

        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDouble(),
                JsonValueKind.String when double.TryParse(value.GetString(), out var parsed) => parsed,
                _ => fallback,
            };
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static bool GetBool(JsonElement root, string key, bool fallback)
    {
        if (!root.TryGetProperty(key, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static double ClampFinite(double value, double min, double max, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value)
            ? fallback
            : Math.Clamp(value, min, max);

    private static double FiniteOrDefault(double value, double fallback) =>
        double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
}
