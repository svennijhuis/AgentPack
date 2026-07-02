namespace AgentPack.Core;

public enum ProviderName
{
    Claude,
    Codex,
    Copilot,
    Cursor
}

public static class ProviderNames
{
    public static readonly IReadOnlyList<ProviderName> All = Enum.GetValues<ProviderName>();

    public static string Display(this ProviderName provider) => provider.ToString().ToLowerInvariant();

    public static bool TryParse(string? value, out ProviderName provider)
    {
        provider = default;
        return !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value.Trim(), ignoreCase: true, out provider);
    }

    public static ProviderName Parse(string value)
    {
        return TryParse(value, out var provider)
            ? provider
            : throw new AgentPackException(
                $"Unknown provider '{value}'.",
                $"Valid providers: {string.Join(", ", All.Select(Display))}.");
    }
}
