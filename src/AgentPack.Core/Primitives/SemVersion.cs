namespace AgentPack.Core.Primitives;

/// <summary>
/// Parsed semantic version. Parsing happens once at the catalog boundary;
/// everything past it compares typed values instead of strings.
/// </summary>
public readonly record struct SemVersion(int Major, int Minor, int Patch, string? Suffix) : IComparable<SemVersion>
{
    public static SemVersion Parse(string value)
    {
        return TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a valid semantic version (expected MAJOR.MINOR.PATCH, e.g. 1.2.0).");
    }

    public static bool TryParse(string? value, out SemVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        var suffixStart = text.IndexOfAny(['-', '+']);
        string? suffix = null;
        var core = text;
        if (suffixStart > 0)
        {
            suffix = text[suffixStart..];
            core = text[..suffixStart];
            if (suffix.Length < 2) return false;
        }

        var parts = core.Split('.');
        if (parts.Length != 3) return false;
        if (!TryParseComponent(parts[0], out var major)) return false;
        if (!TryParseComponent(parts[1], out var minor)) return false;
        if (!TryParseComponent(parts[2], out var patch)) return false;

        version = new SemVersion(major, minor, patch, suffix);
        return true;
    }

    public int CompareTo(SemVersion other)
    {
        var cmp = Major.CompareTo(other.Major);
        if (cmp != 0) return cmp;
        cmp = Minor.CompareTo(other.Minor);
        if (cmp != 0) return cmp;
        cmp = Patch.CompareTo(other.Patch);
        if (cmp != 0) return cmp;

        // A release (no pre-release suffix) is higher than any pre-release of the same core.
        var leftIsPreRelease = Suffix is ['-', ..];
        var rightIsPreRelease = other.Suffix is ['-', ..];
        if (leftIsPreRelease != rightIsPreRelease) return leftIsPreRelease ? -1 : 1;
        return string.CompareOrdinal(Suffix ?? "", other.Suffix ?? "");
    }

    public static bool operator >(SemVersion left, SemVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(SemVersion left, SemVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(SemVersion left, SemVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(SemVersion left, SemVersion right) => left.CompareTo(right) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{Suffix}";

    private static bool TryParseComponent(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || (text.Length > 1 && text[0] == '0')) return false;
        return int.TryParse(text, out value) && value >= 0;
    }
}
