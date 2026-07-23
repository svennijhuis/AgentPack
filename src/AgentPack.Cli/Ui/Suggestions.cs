using System.Text.RegularExpressions;

namespace AgentPack.Cli.Ui;

/// <summary>"Did you mean ...?" support for commands and asset ids.</summary>
public static class Suggestions
{
    private static readonly string[] CommandNames =
    [
        "help", "list", "search", "groups", "install", "submit", "remove", "update", "outdated",
        "status", "diff", "pin", "unpin",
        "doctor", "catalog", "profile"
    ];

    /// <summary>
    /// Commands that existed before the CLI was renamed. Typing the old name is not a
    /// typo, so it must map to its replacement directly — nearest-match would send
    /// 'find' to 'pin' and leave 'add' and 'upgrade' with no hint at all.
    /// </summary>
    private static readonly Dictionary<string, string> Renamed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["add"] = "install",
        ["ls"] = "list",
        ["find"] = "search",
        ["uninstall"] = "remove",
        ["upgrade"] = "update",
        ["plan"] = "install --dry-run",
        ["init"] = "submit",
        ["new"] = "submit",
        ["import"] = "submit",
        ["source"] = "catalog use"
    };

    public static string? ForParseError(string message)
    {
        var match = Regex.Match(message, "'(?<token>[^']+)'");
        if (!match.Success) return null;
        var token = match.Groups["token"].Value;

        if (Renamed.TryGetValue(token, out var replacement))
        {
            return $"'agentpack {token}' is now 'agentpack {replacement}'.";
        }

        return Nearest(token, CommandNames) is { } suggestion
            ? $"Did you mean 'agentpack {suggestion}'?"
            : null;
    }

    public static string? Nearest(string input, IEnumerable<string> candidates)
    {
        var best = candidates
            .Select(candidate => (candidate, distance: Levenshtein(input.ToLowerInvariant(), candidate.ToLowerInvariant())))
            .OrderBy(x => x.distance)
            .FirstOrDefault();

        var threshold = Math.Max(2, input.Length / 3);
        return best.candidate is not null && best.distance <= threshold ? best.candidate : null;
    }

    public static int Levenshtein(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var substitution = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
