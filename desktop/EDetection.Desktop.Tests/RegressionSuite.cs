internal static partial class RegressionSuite
{
    private static readonly IReadOnlyDictionary<string, Func<Task>> Tests =
        new Dictionary<string, Func<Task>>(StringComparer.OrdinalIgnoreCase)
        {
            ["harness"] = HarnessAsync,
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
}
