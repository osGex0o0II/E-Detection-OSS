using System.Globalization;
using System.Numerics;

namespace EDetection.NativeCore.Services;

public static class SemanticVersionComparer
{
    public static int Compare(string left, string right)
    {
        if (!TryParse(left, out var leftVersion))
        {
            throw new FormatException($"Invalid semantic version: {left}");
        }

        if (!TryParse(right, out var rightVersion))
        {
            throw new FormatException($"Invalid semantic version: {right}");
        }

        var coreComparison = leftVersion.Major.CompareTo(rightVersion.Major);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        coreComparison = leftVersion.Minor.CompareTo(rightVersion.Minor);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        coreComparison = leftVersion.Patch.CompareTo(rightVersion.Patch);
        if (coreComparison != 0)
        {
            return coreComparison;
        }

        if (leftVersion.Prerelease.Count == 0 || rightVersion.Prerelease.Count == 0)
        {
            return leftVersion.Prerelease.Count == rightVersion.Prerelease.Count
                ? 0
                : leftVersion.Prerelease.Count == 0 ? 1 : -1;
        }

        var sharedLength = Math.Min(leftVersion.Prerelease.Count, rightVersion.Prerelease.Count);
        for (var index = 0; index < sharedLength; index++)
        {
            var identifierComparison = ComparePrereleaseIdentifier(
                leftVersion.Prerelease[index],
                rightVersion.Prerelease[index]);
            if (identifierComparison != 0)
            {
                return identifierComparison;
            }
        }

        return leftVersion.Prerelease.Count.CompareTo(rightVersion.Prerelease.Count);
    }

    public static bool TryCompare(string left, string right, out int comparison)
    {
        if (!TryParse(left, out _) || !TryParse(right, out _))
        {
            comparison = 0;
            return false;
        }

        comparison = Compare(left, right);
        return true;
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftIsNumeric = TryParseNumericIdentifier(left, out var leftNumber);
        var rightIsNumeric = TryParseNumericIdentifier(right, out var rightNumber);
        if (leftIsNumeric && rightIsNumeric)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (leftIsNumeric != rightIsNumeric)
        {
            return leftIsNumeric ? -1 : 1;
        }

        return StringComparer.Ordinal.Compare(left, right);
    }

    private static bool TryParse(string value, out SemanticVersion version)
    {
        version = default!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var buildSeparator = normalized.IndexOf('+');
        if (buildSeparator >= 0)
        {
            var build = normalized[(buildSeparator + 1)..];
            if (!AreValidIdentifiers(build, allowNumericLeadingZeros: true))
            {
                return false;
            }

            normalized = normalized[..buildSeparator];
        }

        var prereleaseSeparator = normalized.IndexOf('-');
        var prereleaseText = prereleaseSeparator >= 0
            ? normalized[(prereleaseSeparator + 1)..]
            : "";
        var coreText = prereleaseSeparator >= 0
            ? normalized[..prereleaseSeparator]
            : normalized;
        if (prereleaseSeparator >= 0
            && !AreValidIdentifiers(prereleaseText, allowNumericLeadingZeros: false))
        {
            return false;
        }

        var core = coreText.Split('.', StringSplitOptions.None);
        if (core.Length != 3
            || !TryParseCoreIdentifier(core[0], out var major)
            || !TryParseCoreIdentifier(core[1], out var minor)
            || !TryParseCoreIdentifier(core[2], out var patch))
        {
            return false;
        }

        version = new SemanticVersion(
            major,
            minor,
            patch,
            prereleaseSeparator >= 0
                ? prereleaseText.Split('.', StringSplitOptions.None)
                : []);
        return true;
    }

    private static bool AreValidIdentifiers(string value, bool allowNumericLeadingZeros)
    {
        var identifiers = value.Split('.', StringSplitOptions.None);
        return identifiers.Length > 0
            && identifiers.All(identifier =>
                identifier.Length > 0
                && identifier.All(static character =>
                    char.IsAsciiLetterOrDigit(character) || character == '-')
                && (allowNumericLeadingZeros
                    || !IsNumeric(identifier)
                    || identifier.Length == 1
                    || identifier[0] != '0'));
    }

    private static bool TryParseCoreIdentifier(string value, out BigInteger number)
    {
        number = default;
        return (value.Length == 1 || value.Length > 1 && value[0] != '0')
            && TryParseNumericIdentifier(value, out number);
    }

    private static bool TryParseNumericIdentifier(string value, out BigInteger number)
    {
        number = default;
        return IsNumeric(value)
            && BigInteger.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out number);
    }

    private static bool IsNumeric(string value) =>
        value.Length > 0 && value.All(static character => character is >= '0' and <= '9');

    private sealed record SemanticVersion(
        BigInteger Major,
        BigInteger Minor,
        BigInteger Patch,
        IReadOnlyList<string> Prerelease);
}
