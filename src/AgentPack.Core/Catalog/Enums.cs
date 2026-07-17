namespace AgentPack.Core;

public enum AssetKind
{
    Skills,
    Hooks,
    Mcp,
    Tools,
    Instructions,
    Rules,
    Prompts,
    Templates,
    Agents
}

public enum AssetStatus
{
    Experimental,
    Recommended,
    Deprecated,
    Blocked
}

public enum Channel
{
    Internal,
    Beta,
    Stable
}

public enum GroupStatus
{
    Active,
    Deprecated
}

public enum HookTrigger
{
    PreToolUse,
    PostToolUse,
    Stop,
    SessionStart,
    UserPromptSubmit,
    Notification
}

public enum McpTransport
{
    Stdio,
    Http,
    Sse
}

public static class AssetKinds
{
    public static readonly IReadOnlyList<AssetKind> All = Enum.GetValues<AssetKind>();

    public static string Display(this AssetKind kind) => kind.ToString().ToLowerInvariant();

    public static bool TryParse(string? value, out AssetKind kind)
    {
        kind = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim().ToLowerInvariant();
        foreach (var candidate in All)
        {
            var display = candidate.Display();
            if (normalized == display || normalized + "s" == display)
            {
                kind = candidate;
                return true;
            }
        }

        return false;
    }

    public static AssetKind Parse(string value)
    {
        return TryParse(value, out var kind)
            ? kind
            : throw new AgentPackException(
                $"Unknown kind '{value}'.",
                $"Valid kinds: {string.Join(", ", All.Select(Display))}.");
    }
}

public static class EnumParsers
{
    public static AssetStatus ParseStatus(string? value, string context)
        => ParseOrThrow<AssetStatus>(value, AssetStatus.Recommended, context, "status");

    public static Channel ParseChannel(string? value, string context)
        => ParseOrThrow<Channel>(value, Channel.Stable, context, "channel");

    public static GroupStatus ParseGroupStatus(string? value, string context)
        => ParseOrThrow<GroupStatus>(value, GroupStatus.Active, context, "status");

    public static McpTransport ParseTransport(string? value, string context)
    {
        if (string.IsNullOrWhiteSpace(value)) return McpTransport.Stdio;
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == "local") return McpTransport.Stdio;
        return ParseOrThrow<McpTransport>(normalized, McpTransport.Stdio, context, "transport");
    }

    public static HookTrigger ParseTrigger(string? value, string context)
    {
        if (string.IsNullOrWhiteSpace(value)) return HookTrigger.PreToolUse;
        var compact = value.Trim().Replace("-", "").Replace("_", "").ToLowerInvariant();
        return compact switch
        {
            "pretooluse" or "pretool" or "beforetool" or "beforetooluse" or "beforeedit" or "beforefileedit" => HookTrigger.PreToolUse,
            "posttooluse" or "posttool" or "aftertool" or "aftertooluse" or "afteredit" or "afterfileedit" => HookTrigger.PostToolUse,
            "stop" or "sessionend" => HookTrigger.Stop,
            "sessionstart" => HookTrigger.SessionStart,
            "userpromptsubmit" => HookTrigger.UserPromptSubmit,
            "notification" => HookTrigger.Notification,
            _ => throw new AgentPackException(
                $"{context}: unknown hook trigger '{value}'.",
                $"Valid triggers: {string.Join(", ", Enum.GetValues<HookTrigger>().Select(x => CamelCase(x.ToString())))}.")
        };
    }

    public static string CamelCase(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private static T ParseOrThrow<T>(string? value, T fallback, string context, string fieldName) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed)) return parsed;
        throw new AgentPackException(
            $"{context}: unknown {fieldName} '{value}'.",
            $"Valid values: {string.Join(", ", Enum.GetValues<T>().Select(x => x.ToString().ToLowerInvariant()))}.");
    }
}
