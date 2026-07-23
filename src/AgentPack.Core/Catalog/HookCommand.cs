namespace AgentPack.Core;

/// <summary>
/// A hook's entry file is a relative path inside its own reviewed content folder.
/// Catalog validation, submission, and post-resolve verification all need the same
/// rule, so it is defined once here rather than re-derived at each call site.
/// </summary>
public static class HookCommand
{
    /// <summary>
    /// The command as forward-slash segments, or null when it escapes its content
    /// folder (absolute, rooted, empty, or containing '..').
    /// </summary>
    public static string? Normalize(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var trimmed = command.Trim();
        var segments = trimmed.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(trimmed) || segments.Length == 0 || segments.Contains("..")) return null;
        return string.Join('/', segments);
    }

    /// <summary>
    /// The entry file's full path under <paramref name="contentRoot"/>, or null when the
    /// command is unsafe, escapes the root, or names a file that is not there.
    /// </summary>
    public static string? ResolveInside(string contentRoot, string? command)
    {
        if (Normalize(command) is not { } normalized) return null;

        var root = Path.GetFullPath(contentRoot);
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(normalized.Split('/'))));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? full : null;
    }
}
