namespace AgentPack.Core.Primitives;

/// <summary>
/// Small, deliberately strict semantic-version range used by catalog references.
/// Supports exact versions and whitespace-separated &gt;, &gt;=, &lt;, &lt;=, = comparisons.
/// </summary>
public sealed record SemVersionRange
{
    private readonly IReadOnlyList<Constraint> _constraints;

    private SemVersionRange(string text, IReadOnlyList<Constraint> constraints)
    {
        Text = text;
        _constraints = constraints;
    }

    public string Text { get; }

    public bool Contains(SemVersion version) => _constraints.All(x => x.Matches(version));

    public static SemVersionRange Parse(string value) => TryParse(value, out var range)
        ? range!
        : throw new FormatException($"Invalid semantic-version range '{value}'.");

    public static bool TryParse(string? value, out SemVersionRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        var constraints = new List<Constraint>();
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var op = token.StartsWith(">=") || token.StartsWith("<=") ? token[..2]
                : token.StartsWith('>') || token.StartsWith('<') || token.StartsWith('=') ? token[..1]
                : "=";
            var versionText = op == "=" && !token.StartsWith('=') ? token : token[op.Length..];
            if (!SemVersion.TryParse(versionText, out var version)) return false;
            constraints.Add(new Constraint(op, version));
        }

        if (constraints.Count == 0) return false;
        range = new SemVersionRange(text, constraints);
        return true;
    }

    public override string ToString() => Text;

    private sealed record Constraint(string Operator, SemVersion Version)
    {
        public bool Matches(SemVersion candidate) => Operator switch
        {
            ">" => candidate > Version,
            ">=" => candidate >= Version,
            "<" => candidate < Version,
            "<=" => candidate <= Version,
            "=" => candidate.CompareTo(Version) == 0,
            _ => false
        };
    }
}
