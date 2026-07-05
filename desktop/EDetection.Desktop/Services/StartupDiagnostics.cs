namespace EDetection.Desktop.Services;

internal static class StartupDiagnostics
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "E-Detection",
        "Desktop");

    private static readonly string LogPath = Path.Combine(LogDirectory, "startup.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            lock (Gate)
            {
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }

    public static void WriteException(string scope, Exception exception) =>
        Write($"{scope}: {exception}");
}
