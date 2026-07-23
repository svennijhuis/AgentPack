namespace AgentPack.Core;

/// <summary>Product defaults that make the public catalog usable without setup.</summary>
public static class AgentPackDefaults
{
    public const string OfficialCatalogName = "official";
    public const string OfficialCatalogUrl = "https://github.com/svennijhuis/AgentPack.git";

    /// <summary>
    /// The built-in catalog may be replaced by an organization or disabled for
    /// hermetic/offline environments. AGENTPACK_CATALOG_URL remains the normal
    /// organization-wide override and is handled by <see cref="SourceManager"/>.
    /// </summary>
    public static AgentPackSource? OfficialCatalog()
    {
        if (Environment.GetEnvironmentVariable("AGENTPACK_DISABLE_DEFAULT_CATALOG") == "1") return null;
        var url = Environment.GetEnvironmentVariable("AGENTPACK_DEFAULT_CATALOG_URL") ?? OfficialCatalogUrl;
        return new AgentPackSource { Name = OfficialCatalogName, Url = url, Branch = "main" };
    }
}
