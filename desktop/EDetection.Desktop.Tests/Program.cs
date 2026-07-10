using System.Text;
using EDetection.Desktop.Models;
using EDetection.Desktop.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

if (args.Length is 1 or 2)
{
    var inputDirectory = Path.GetFullPath(args[0]);
    var configPath = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
    var events = new List<DetectionBackendEvent>();
    var exitCode = await new NativeDetectionBackendService().RunDetectionAsync(
        new DetectionRequest
        {
            InputDirectory = inputDirectory,
            ConfigPath = configPath,
            WriteReport = false,
        },
        new Progress<DetectionBackendEvent>(events.Add),
        CancellationToken.None);
    Assert(exitCode == 0, $"Native detection failed for '{inputDirectory}'.");
    var completed = events.LastOrDefault(evt => evt.EventName == "run_completed")
        ?? throw new InvalidOperationException("Native detection did not emit a completion event.");
    Console.WriteLine($"Native real-data smoke passed: files={completed.TotalFiles}; anomalies={completed.AnomalyFiles}; skipped={completed.SkippedFiles}.");
    return;
}

var root = Path.Combine(Path.GetTempPath(), $"EDetectionNativeSmoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    File.WriteAllText(Path.Combine(root, "config.json"), "{}", Encoding.UTF8);
    File.WriteAllText(Path.Combine(root, "voltage.csv"), "time,Uab,Ubc,Uca\n0,380,380,380\n1,320,380,380\n", Encoding.UTF8);

    var diagnostics = new DesktopDiagnosticsService();
    var issues = diagnostics.ValidateRunInputs(
        new DesktopDiagnosticsRequest(root, string.Empty, Path.Combine(root, "config.json"), WriteReport: false),
        prepareOutputDirectory: false);
    Assert(issues.Count == 0, $"Native preflight should accept a valid fixture: {string.Join("; ", issues)}");
    Assert(DetectionBackendServiceFactory.CreateDefault() is NativeDetectionBackendService,
        "The desktop backend must be the native .NET implementation.");
    Assert(!new AppSettings().EnablePoetryStatus,
        "A new engineering workspace should not enable the optional poetry feed by default.");

    var events = new List<DetectionBackendEvent>();
    var exitCode = await new NativeDetectionBackendService().RunDetectionAsync(
        new DetectionRequest { InputDirectory = root, WriteReport = false },
        new Progress<DetectionBackendEvent>(events.Add),
        CancellationToken.None);
    Assert(exitCode == 0, "Native detection should complete successfully.");
    Assert(events.Any(evt => evt.EventName == "run_completed"), "Native run should emit completion.");
    Assert(events.Any(evt => evt.EventName == "file_result"), "Native run should emit a file result.");

    Console.WriteLine("EDetection native desktop smoke tests passed.");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
