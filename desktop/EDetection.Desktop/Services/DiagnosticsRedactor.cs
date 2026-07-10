using System.Text.RegularExpressions;

namespace EDetection.Desktop.Services;

public static class DiagnosticsRedactor
{
    private static readonly Regex UrlCredentialPattern = new(
        @"\b(?<scheme>https?|ftp)://(?<credential>[^/\s:@]+:[^@\s/]+)@",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuotedWindowsPathPattern = new(
        @"(?<quote>[""'])(?<path>[A-Za-z]:\\[^""'\r\n]+)\k<quote>",
        RegexOptions.Compiled);

    private static readonly Regex UnquotedWindowsPathPattern = new(
        @"(?<![\w%{}])(?<path>[A-Za-z]:\\[^\s\r\n\t<>|""']+)",
        RegexOptions.Compiled);

    private static readonly Regex UncPathPattern = new(
        @"(?<![\w%{}])\\\\[^\s\r\n\t<>|""']+",
        RegexOptions.Compiled);

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var redacted = UrlCredentialPattern.Replace(
            text,
            match => $"{match.Groups["scheme"].Value}://<redacted>@");

        foreach (var replacement in BuildPathReplacements())
        {
            redacted = ReplaceIgnoreCase(redacted, replacement.Source, replacement.Target);
        }

        redacted = QuotedWindowsPathPattern.Replace(
            redacted,
            match => $"{match.Groups["quote"].Value}{BuildPathPlaceholder(match.Groups["path"].Value)}{match.Groups["quote"].Value}");
        redacted = UnquotedWindowsPathPattern.Replace(
            redacted,
            match => BuildPathPlaceholder(match.Groups["path"].Value));
        redacted = UncPathPattern.Replace(redacted, "{networkPath}");
        return redacted;
    }

    private static IReadOnlyList<PathReplacement> BuildPathReplacements()
    {
        var replacements = new List<PathReplacement>();
        AddSpecialFolder(replacements, Environment.SpecialFolder.UserProfile, "%USERPROFILE%");
        AddSpecialFolder(replacements, Environment.SpecialFolder.LocalApplicationData, "%LOCALAPPDATA%");
        AddSpecialFolder(replacements, Environment.SpecialFolder.ApplicationData, "%APPDATA%");
        AddEnvironmentPath(replacements, "TEMP", "%TEMP%");
        AddEnvironmentPath(replacements, "TMP", "%TEMP%");
        AddEnvironmentPath(replacements, "ProgramFiles", "%ProgramFiles%");
        AddEnvironmentPath(replacements, "ProgramFiles(x86)", "%ProgramFiles(x86)%");
        AddPath(replacements, AppContext.BaseDirectory, "{app}");

        return replacements
            .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Source.Length)
            .ToList();
    }

    private static void AddSpecialFolder(
        List<PathReplacement> replacements,
        Environment.SpecialFolder folder,
        string target)
    {
        AddPath(replacements, Environment.GetFolderPath(folder), target);
    }

    private static void AddEnvironmentPath(
        List<PathReplacement> replacements,
        string name,
        string target)
    {
        AddPath(replacements, Environment.GetEnvironmentVariable(name), target);
    }

    private static void AddPath(
        List<PathReplacement> replacements,
        string? source,
        string target)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }

        var fullPath = TryGetFullPath(source);
        if (string.IsNullOrWhiteSpace(fullPath) || fullPath.Length < 3)
        {
            return;
        }

        replacements.Add(new PathReplacement(
            TrimTrailingSeparators(fullPath),
            target));
    }

    private static string ReplaceIgnoreCase(
        string text,
        string oldValue,
        string newValue)
    {
        return Regex.Replace(
            text,
            Regex.Escape(oldValue),
            newValue.Replace("$", "$$"),
            RegexOptions.IgnoreCase);
    }

    private static string BuildPathPlaceholder(string path)
    {
        var trimmedPath = path.TrimEnd('.', ',', ';', ':', ')', ']');
        var suffix = path[trimmedPath.Length..];
        var fileName = Path.GetFileName(trimmedPath);
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Contains('\\')
            || fileName.Contains(':'))
        {
            return "{localPath}" + suffix;
        }

        return $"{{localPath}}\\{fileName}{suffix}";
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root)
            && root.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private sealed record PathReplacement(string Source, string Target);
}
