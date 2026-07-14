namespace AgentPack.Core;

/// <summary>
/// Group labels are hierarchical: a top-level id (<c>csharp</c>) may have
/// slash-delimited subgroups (<c>csharp/review</c>). Filtering and profile
/// selection match a filter against an asset's group either exactly or as a
/// path prefix, so <c>-g csharp</c> selects every <c>csharp/*</c> asset while
/// <c>-g csharp/review</c> narrows to that subgroup.
/// </summary>
public static class GroupMatch
{
    /// <summary>True when <paramref name="assetGroup"/> is the filter itself or a subgroup of it.</summary>
    public static bool Matches(string filter, string assetGroup) =>
        assetGroup.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
        assetGroup.StartsWith(filter + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The top-level segment of a group label — <c>csharp/review</c> -> <c>csharp</c>.</summary>
    public static string TopLevel(string group)
    {
        var slash = group.IndexOf('/');
        return slash < 0 ? group : group[..slash];
    }
}
