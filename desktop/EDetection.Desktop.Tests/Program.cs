using System.Text;
using EDetection.NativeCore.Models;
using EDetection.NativeCore.Services;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

Console.Error.WriteLine("[native-smoke] stage=process_started target=net10.0");
AssertNoUiRuntimeLoaded();

if (args is ["--regression", var regressionName])
{
    await RegressionSuite.RunAsync(regressionName);
    return;
}

if (args.Length is 1 or 2)
{
    var inputDirectory = Path.GetFullPath(args[0]);
    var configPath = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
    var events = new List<DetectionBackendEvent>();
    Console.Error.WriteLine("[native-smoke] stage=real_data_preflight");
    var exitCode = await new NativeDetectionBackendService().RunDetectionAsync(
        new DetectionRequest
        {
            InputDirectory = inputDirectory,
            ConfigPath = configPath,
            WriteReport = false,
        },
        new SynchronousProgress<DetectionBackendEvent>(events.Add),
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
    Console.Error.WriteLine("[native-smoke] stage=fixture_created");
    File.WriteAllText(Path.Combine(root, "config.json"), "{}", Encoding.UTF8);
    File.WriteAllText(Path.Combine(root, "voltage.csv"), "time,Uab,Ubc,Uca\n0,380,380,380\n1,320,380,380\n", Encoding.UTF8);

    Assert(DetectionBackendServiceFactory.CreateDefault() is NativeDetectionBackendService,
        "The desktop backend must be the native .NET implementation.");

    var events = new List<DetectionBackendEvent>();
    Console.Error.WriteLine("[native-smoke] stage=detection_started files=1");
    var exitCode = await new NativeDetectionBackendService().RunDetectionAsync(
        new DetectionRequest { InputDirectory = root, WriteReport = false },
        new SynchronousProgress<DetectionBackendEvent>(events.Add),
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

static void AssertNoUiRuntimeLoaded()
{
    var forbiddenAssemblies = new[] { "Microsoft.WindowsAppRuntime", "Microsoft.UI.Xaml", "WinUIEx" };
    var loadedForbiddenAssembly = AppDomain.CurrentDomain
        .GetAssemblies()
        .Select(assembly => assembly.GetName().Name)
        .FirstOrDefault(name => name is not null && forbiddenAssemblies.Any(forbidden => name.StartsWith(forbidden, StringComparison.Ordinal)));
    Assert(loadedForbiddenAssembly is null, $"Native smoke must not load a UI runtime. Loaded '{loadedForbiddenAssembly}'.");
    Console.Error.WriteLine("[native-smoke] stage=ui_runtime_isolation_verified");
}

sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
