namespace EDetection.Desktop.Models;

public sealed class CommandPaletteAction(
    string title,
    string description,
    string glyph,
    string category,
    Func<Task> executeAsync,
    Func<bool>? canExecute = null)
{
    public string Title { get; } = title;

    public string Description { get; } = description;

    public string Glyph { get; } = glyph;

    public string Category { get; } = category;

    public bool IsEnabled => canExecute?.Invoke() ?? true;

    public Task ExecuteAsync() => executeAsync();

    public int MatchScore(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        if (Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 30;
        }

        if (Category.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 20;
        }

        if (Description.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return 10;
        }

        return -1;
    }
}
