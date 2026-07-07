namespace EDetection.Desktop.Services;

internal static class TabularExportSanitizer
{
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@'];

    public static string Cell(string? value)
    {
        var text = (value ?? "")
            .ReplaceLineEndings(" ")
            .Replace('\t', ' ')
            .TrimEnd();

        var trimmedStart = text.TrimStart();
        if (trimmedStart.Length > 0
            && FormulaPrefixes.Contains(trimmedStart[0]))
        {
            return $"'{text}";
        }

        return text;
    }
}
