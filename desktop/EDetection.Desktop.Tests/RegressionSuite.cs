using System.Text;
using EDetection.NativeCore.Models;
using EDetection.NativeCore.Services;

internal static partial class RegressionSuite
{
    private static readonly IReadOnlyDictionary<string, Func<Task>> Tests =
        new Dictionary<string, Func<Task>>(StringComparer.OrdinalIgnoreCase)
        {
            ["blank-key-values"] = BlankKeyValuesAsync,
            ["harness"] = HarnessAsync,
            ["inverted-temperature-thresholds"] = InvertedTemperatureThresholdsAsync,
            ["inverted-voltage-thresholds"] = InvertedVoltageThresholdsAsync,
            ["non-finite-key-values"] = NonFiniteKeyValuesAsync,
            ["reparse-child-is-not-followed"] = ReparseChildIsNotFollowedAsync,
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
        CancellationToken cancellationToken = default)
    {
        var events = new List<DetectionBackendEvent>();
        var exitCode = await new NativeDetectionBackendService().RunDetectionAsync(
            new DetectionRequest
            {
                InputDirectory = inputDirectory,
                ConfigPath = configPath,
                WriteReport = false,
            },
            new CollectingProgress<DetectionBackendEvent>(events),
            cancellationToken);
        return (exitCode, events);
    }
}
