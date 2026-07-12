using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using EDetection.NativeCore.Models;
using EDetection.NativeCore.Services;

internal static partial class RegressionSuite
{
    private static readonly IReadOnlyDictionary<string, Func<Task>> Tests =
        new Dictionary<string, Func<Task>>(StringComparer.OrdinalIgnoreCase)
        {
            ["blank-key-values"] = BlankKeyValuesAsync,
            ["backend-runs-on-worker-thread"] = BackendRunsOnWorkerThreadAsync,
            ["cancellation-interrupts-large-file"] = CancellationInterruptsLargeFileAsync,
            ["chronological-device-times"] = ChronologicalDeviceTimesAsync,
            ["harness"] = HarnessAsync,
            ["inverted-temperature-thresholds"] = InvertedTemperatureThresholdsAsync,
            ["inverted-voltage-thresholds"] = InvertedVoltageThresholdsAsync,
            ["non-finite-key-values"] = NonFiniteKeyValuesAsync,
            ["report-publication-failure-is-clean"] = ReportPublicationFailureIsCleanAsync,
            ["reparse-child-is-not-followed"] = ReparseChildIsNotFollowedAsync,
            ["semantic-version-precedence"] = SemanticVersionPrecedenceAsync,
        };

    public static async Task RunAsync(string name)
    {
        IReadOnlyList<KeyValuePair<string, Func<Task>>> selected;
        if (name.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            selected = Tests.OrderBy(static pair => pair.Key).ToList();
        }
        else if (Tests.TryGetValue(name, out var test))
        {
            selected = [new KeyValuePair<string, Func<Task>>(name, test)];
        }
        else
        {
            throw new ArgumentException(
                $"Unknown regression '{name}'. Available: {string.Join(", ", Tests.Keys.Order())}",
                nameof(name));
        }

        foreach (var (testName, test) in selected)
        {
            await test();
            Console.WriteLine($"PASS {testName}");
        }
    }

    private static Task HarnessAsync()
    {
        var root = TestSupport.CreateFixtureDirectory("Harness");
        try
        {
            TestSupport.True(Directory.Exists(root), "Fixture directory should exist.");
        }
        finally
        {
            TestSupport.DeleteFixtureDirectory(root);
        }

        return Task.CompletedTask;
    }

    private static async Task BlankKeyValuesAsync()
    {
        var root = TestSupport.CreateFixtureDirectory("BlankKeyValues");
        try
        {
            File.WriteAllText(
                Path.Combine(root, "blank.csv"),
                "time,Uab,Ubc,Uca\n0,,,\n1,,,\n2,,,\n",
                Encoding.UTF8);
            var (_, events) = await RunBackendAsync(root);
            var result = events.Single(e => e.EventName == "file_result");
            TestSupport.Equal("anomaly", result.Status, "Blank key measurements must be offline.");
            TestSupport.True(
                result.AnomalyTypes?.Contains("设备离线", StringComparison.Ordinal) is true,
                "Blank key measurements must produce an offline anomaly.");
        }
        finally
        {
            TestSupport.DeleteFixtureDirectory(root);
        }
    }

    private static Task BackendRunsOnWorkerThreadAsync()
    {
        var callerThreadId = Environment.CurrentManagedThreadId;
        var backend = new RecordingBackend();
        var exitCode = DetectionExecutionService.RunAsync(
                backend,
                new DetectionRequest { InputDirectory = Path.GetTempPath(), WriteReport = false },
                new CollectingProgress<DetectionBackendEvent>([]),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        TestSupport.Equal(0, exitCode, "The execution service must preserve the backend exit code.");
        TestSupport.True(
            backend.ThreadId != callerThreadId,
            "The detection backend must not run on the caller/UI thread.");
        return Task.CompletedTask;
    }

    private static async Task CancellationInterruptsLargeFileAsync()
    {
        var root = TestSupport.CreateFixtureDirectory("LargeFileCancellation");
        try
        {
            var csvPath = Path.Combine(root, "large.csv");
            await using (var writer = new StreamWriter(csvPath, append: false, Encoding.UTF8))
            {
                await writer.WriteLineAsync("time,Uab,Ubc,Uca,Ia,Ib,Ic");
                for (var index = 0; index < 300_000; index++)
                {
                    await writer.WriteLineAsync($"{index},380,381,379,10,11,9");
                }
            }

            using var cancellation = new CancellationTokenSource();
            var run = new NativeDetectionBackendService().RunDetectionAsync(
                new DetectionRequest { InputDirectory = root, WriteReport = false },
                new CollectingProgress<DetectionBackendEvent>([]),
                cancellation.Token);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException(
                    "Cancellation during a large single-file analysis must throw OperationCanceledException.");
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            TestSupport.DeleteFixtureDirectory(root);
        }
    }

    private static async Task ChronologicalDeviceTimesAsync()
    {
        var root = TestSupport.CreateFixtureDirectory("ChronologicalDeviceTimes");
        var output = Path.Combine(root, "reports");
        try
        {
            File.WriteAllText(
                Path.Combine(root, "20260712_1TM.csv"),
                "time,Uab,Ubc,Uca\n01:00,340,380,380\n23:00,200,380,380\n",
                Encoding.UTF8);
            var (exitCode, events) = await RunBackendAsync(
                root,
                outputDirectory: output,
                writeReport: true);
            TestSupport.Equal(0, exitCode, "The chronology fixture must produce a report.");
            var reportPath = events.Single(e => e.EventName == "report_written").ReportPath
                ?? throw new InvalidOperationException("The report-written event must include a path.");

            using var archive = ZipFile.OpenRead(reportPath);
            var entry = archive.GetEntry("xl/worksheets/sheet3.xml")
                ?? throw new InvalidOperationException("The report must contain the device summary worksheet.");
            using var stream = entry.Open();
            var worksheet = XDocument.Load(stream);
            TestSupport.Equal(
                "2026-07-12 01:00",
                ReadWorksheetCell(worksheet, "F2"),
                "The first anomaly time must be the earliest timestamp, independent of severity order.");
            TestSupport.Equal(
                "2026-07-12 23:00",
                ReadWorksheetCell(worksheet, "G2"),
                "The last anomaly time must be the latest timestamp, independent of severity order.");
        }
        finally
        {
            TestSupport.DeleteFixtureDirectory(root);
        }
    }

    private static async Task ReportPublicationFailureIsCleanAsync()
    {
        var root = TestSupport.CreateFixtureDirectory("ReportPublicationFailure");
        var output = Path.Combine(root, "reports");
        Directory.CreateDirectory(output);
        try
        {
            var csvPath = Path.Combine(root, "20260712_1TM.csv");
            await using (var writer = new StreamWriter(csvPath, append: false, Encoding.UTF8))
            {
                await writer.WriteLineAsync("time,Uab,Ubc,Uca");
                for (var index = 0; index < 10_000; index++)
                {
                    await writer.WriteLineAsync($"{index},340,380,380");
                }
            }

            using var finalPathBlocked = new ManualResetEventSlim();
            using var watcher = new FileSystemWatcher(output, "*.tmp")
            {
                NotifyFilter = NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, args) =>
            {
                var temporaryName = Path.GetFileName(args.FullPath);
                if (!temporaryName.StartsWith(".", StringComparison.Ordinal)
                    || !temporaryName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var withoutWrapper = temporaryName[1..^4];
                var uniqueSuffix = withoutWrapper.LastIndexOf('.');
                if (uniqueSuffix <= 0)
                {
                    return;
                }

                Directory.CreateDirectory(Path.Combine(output, withoutWrapper[..uniqueSuffix]));
                finalPathBlocked.Set();
            };

            var (exitCode, _) = await RunBackendAsync(
                root,
                outputDirectory: output,
                writeReport: true);
            TestSupport.Equal(1, exitCode, "A blocked final report path must fail publication.");
            TestSupport.True(finalPathBlocked.IsSet, "The fixture must inject a final publication failure.");
            TestSupport.Equal(
                0,
                Directory.EnumerateFiles(output, "*.xlsx", SearchOption.TopDirectoryOnly).Count(),
                "A publication failure must not leave a final-name workbook.");
            TestSupport.Equal(
                0,
                Directory.EnumerateFiles(output, "*.tmp", SearchOption.TopDirectoryOnly).Count(),
                "A publication failure must remove temporary workbooks.");
        }
        finally
        {
            TestSupport.DeleteFixtureDirectory(root);
        }
    }

    private static Task SemanticVersionPrecedenceAsync()
    {
        TestSupport.True(SemanticVersionComparer.Compare("2.0.1", "2.0.1-fix") > 0, "A stable version must outrank its prerelease.");
        TestSupport.True(SemanticVersionComparer.Compare("2.0.1-fix", "2.0.1") < 0, "A prerelease must rank below its stable version.");
        TestSupport.Equal(0, SemanticVersionComparer.Compare("2.0.1+build.7", "2.0.1+build.9"), "Build metadata must not affect precedence.");
        TestSupport.True(SemanticVersionComparer.Compare("2.0.1-rc.10", "2.0.1-rc.2") > 0, "Numeric prerelease identifiers must compare numerically.");
        TestSupport.True(SemanticVersionComparer.Compare("2.0.1-1", "2.0.1-beta") < 0, "Numeric prerelease identifiers must rank below text identifiers.");
        TestSupport.True(SemanticVersionComparer.Compare("2.0.1-alpha", "2.0.1-alpha.1") < 0, "A shorter equal prerelease must rank lower.");
        TestSupport.True(SemanticVersionComparer.Compare("v2.0.2", "2.0.1") > 0, "A leading release-tag v must be accepted.");
        return Task.CompletedTask;
    }

    private static async Task NonFiniteKeyValuesAsync()
    {
        var root = TestSupport.CreateFixtureDirectory("NonFiniteKeyValues");
        try
        {
            File.WriteAllText(
                Path.Combine(root, "non-finite.csv"),
                "time,Uab,Ubc,Uca\n0,NaN,NaN,NaN\n1,Infinity,-Infinity,NaN\n",
                Encoding.UTF8);
            var (_, events) = await RunBackendAsync(root);
            var result = events.Single(e => e.EventName == "file_result");
            TestSupport.Equal("anomaly", result.Status, "Non-finite key measurements must be offline.");
            TestSupport.True(
                result.AnomalyTypes?.Contains("设备离线", StringComparison.Ordinal) is true,
                "Non-finite key measurements must produce an offline anomaly.");
        }
        finally
        {
            TestSupport.DeleteFixtureDirectory(root);
        }
    }

    private static Task InvertedVoltageThresholdsAsync() =>
        AssertInvertedThresholdsRejectedAsync(
            "InvertedVoltageThresholds",
            "{\"V_MIN_THRESHOLD\":500,\"V_MAX_THRESHOLD\":300}",
            "电压");

    private static Task InvertedTemperatureThresholdsAsync() =>
        AssertInvertedThresholdsRejectedAsync(
            "InvertedTemperatureThresholds",
            "{\"T_MIN_THRESHOLD\":80,\"T_MAX_THRESHOLD\":20}",
            "温度");

    private static async Task AssertInvertedThresholdsRejectedAsync(
        string fixtureName,
        string configJson,
        string expectedMessage)
    {
        var root = TestSupport.CreateFixtureDirectory(fixtureName);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "normal.csv"),
                "time,Uab,Ubc,Uca\n0,380,380,380\n",
                Encoding.UTF8);
            var configPath = Path.Combine(root, "thresholds.json");
            File.WriteAllText(configPath, configJson, Encoding.UTF8);
            var (exitCode, events) = await RunBackendAsync(root, configPath);
            TestSupport.Equal(1, exitCode, "Inverted thresholds must reject the run.");
            TestSupport.True(
                events.Any(e => e.EventName == "error"
                    && (e.Message?.Contains(expectedMessage, StringComparison.Ordinal) ?? false)),
                "The configuration error should identify the inverted threshold family.");
        }
        finally
        {
            TestSupport.DeleteFixtureDirectory(root);
        }
    }

    private static async Task ReparseChildIsNotFollowedAsync()
    {
        var root = TestSupport.CreateFixtureDirectory("ReparseRoot");
        var outside = TestSupport.CreateFixtureDirectory("ReparseOutside");
        try
        {
            File.WriteAllText(
                Path.Combine(root, "inside.csv"),
                "time,Uab,Ubc,Uca\n0,380,380,380\n",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(outside, "outside.csv"),
                "time,Uab,Ubc,Uca\n0,320,380,380\n",
                Encoding.UTF8);
            Directory.CreateSymbolicLink(Path.Combine(root, "linked-outside"), outside);

            var (_, events) = await RunBackendAsync(root);
            var started = events.Single(e => e.EventName == "run_started");
            TestSupport.Equal(1, started.TotalFiles, "Input discovery must not follow reparse points.");
        }
        finally
        {
            TestSupport.DeleteFixtureDirectory(root);
            TestSupport.DeleteFixtureDirectory(outside);
        }
    }

    private static async Task<(int ExitCode, List<DetectionBackendEvent> Events)> RunBackendAsync(
        string inputDirectory,
        string? configPath = null,
        string? outputDirectory = null,
        bool writeReport = false,
        CancellationToken cancellationToken = default)
    {
        var events = new List<DetectionBackendEvent>();
        var exitCode = await new NativeDetectionBackendService().RunDetectionAsync(
            new DetectionRequest
            {
                InputDirectory = inputDirectory,
                ConfigPath = configPath,
                OutputDirectory = outputDirectory,
                WriteReport = writeReport,
            },
            new CollectingProgress<DetectionBackendEvent>(events),
            cancellationToken);
        return (exitCode, events);
    }

    private static string ReadWorksheetCell(XDocument worksheet, string reference)
    {
        XNamespace spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var cell = worksheet
            .Descendants(spreadsheet + "c")
            .Single(element => string.Equals(
                (string?)element.Attribute("r"),
                reference,
                StringComparison.Ordinal));
        return cell.Descendants(spreadsheet + "t").SingleOrDefault()?.Value
            ?? cell.Descendants(spreadsheet + "v").SingleOrDefault()?.Value
            ?? "";
    }

    private sealed class RecordingBackend : IDetectionBackendService
    {
        public int ThreadId { get; private set; }

        public Task<int> RunDetectionAsync(
            DetectionRequest request,
            IProgress<DetectionBackendEvent> progress,
            CancellationToken cancellationToken)
        {
            ThreadId = Environment.CurrentManagedThreadId;
            return Task.FromResult(0);
        }
    }
}
