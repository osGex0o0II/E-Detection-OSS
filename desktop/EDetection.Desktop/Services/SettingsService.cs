using System.Text.Json;
using EDetection.Desktop.Models;

namespace EDetection.Desktop.Services;

public sealed class SettingsService
{
    public const int CurrentSettingsVersion = SettingsServiceVersion.Current;
    public const string SettingsDirectoryEnvironmentVariable = "EDETECTION_DESKTOP_SETTINGS_DIR";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static readonly string SettingsDirectory = ResolveSettingsDirectory();

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public string StorePath => SettingsPath;

    public string StoreStatusText => $"设置存储 v{CurrentSettingsVersion} · {SettingsPath}";

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return CreateDefaultSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? CreateDefaultSettings();
            if (Migrate(settings))
            {
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return CreateDefaultSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        CleanupStaleTemporaryFiles();
        settings.SettingsVersion = CurrentSettingsVersion;
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        WriteAtomic(SettingsPath, json);
    }

    public AppSettings CreateDefault() => CreateDefaultSettings();

    private static AppSettings CreateDefaultSettings() => new()
    {
        SettingsVersion = CurrentSettingsVersion,
    };

    private static bool Migrate(AppSettings settings)
    {
        var changed = settings.SettingsVersion != CurrentSettingsVersion;

        if (settings.SettingsVersion < 6)
        {
            if (string.IsNullOrWhiteSpace(settings.NtfyServerUrl))
            {
                settings.NtfyServerUrl = "https://ntfy.sh";
            }

            settings.SelectedNtfyPriorityIndex = Math.Clamp(settings.SelectedNtfyPriorityIndex, 0, 4);
            if (string.IsNullOrWhiteSpace(settings.UpdateFeedUrl))
            {
                settings.UpdateFeedUrl = "https://github.com/osGex0o0II/E-Detection-OSS/releases/latest";
            }
        }

        if (settings.SettingsVersion < 8)
        {
            settings.PoetryServiceUrl = "https://poetry.palemoky.com/";
            settings.SelectedPoetryLanguageIndex = 0;
        }

        if (string.IsNullOrWhiteSpace(settings.PoetryServiceUrl))
        {
            settings.PoetryServiceUrl = "https://poetry.palemoky.com/";
        }

        settings.SelectedPoetryLanguageIndex = Math.Clamp(settings.SelectedPoetryLanguageIndex, 0, 1);
        settings.SettingsVersion = CurrentSettingsVersion;
        return changed;
    }

    private static string ResolveSettingsDirectory()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(SettingsDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return Path.GetFullPath(overrideDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "E-Detection",
            "Desktop");
    }

    private static void CleanupStaleTemporaryFiles()
    {
        if (!Directory.Exists(SettingsDirectory))
        {
            return;
        }

        foreach (var tempFile in Directory.EnumerateFiles(SettingsDirectory, "settings.json.*.tmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(tempFile) < DateTime.UtcNow.AddDays(-1))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
            }
        }
    }

    private static void WriteAtomic(string path, string contents)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, contents);
        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            try
            {
                File.Delete(tempPath);
            }
            catch
            {
            }

            throw;
        }
    }
}
