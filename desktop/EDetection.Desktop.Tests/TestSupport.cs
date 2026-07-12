internal static class TestSupport
{
    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"{message} Expected={expected} Actual={actual}");
        }
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static string CreateFixtureDirectory(string name)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"EDetection-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteFixtureDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class CollectingProgress<T>(ICollection<T> target) : IProgress<T>
{
    public void Report(T value) => target.Add(value);
}
