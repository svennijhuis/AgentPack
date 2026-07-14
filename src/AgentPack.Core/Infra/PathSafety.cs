namespace AgentPack.Core;

/// <summary>Prevents catalog-controlled relative paths from escaping their declared root.</summary>
public static class PathSafety
{
    public static string ResolveUnderRoot(string root, string relativePath, string context)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw Invalid(context, relativePath);
        }

        var segments = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw Invalid(context, relativePath);
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var resolved = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!resolved.Equals(fullRoot, comparison) &&
            !resolved.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
        {
            throw Invalid(context, relativePath);
        }

        return resolved;
    }

    private static AgentPackException Invalid(string context, string path) => new(
        $"{context} path '{path}' escapes its allowed root.",
        "Use a relative path inside the catalog or checked-out repository; '..' and absolute paths are not allowed.",
        ExitCodes.ValidationFailed);
}
