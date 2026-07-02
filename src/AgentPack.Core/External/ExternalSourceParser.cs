using System.Text.RegularExpressions;

namespace AgentPack.Core;

/// <summary>A git-cloneable repo URL plus the pinned ref and the path inside the repo.</summary>
public sealed record ResolvedExternalSource(string Repo, string Ref, string Path, string? Checksum);

/// <summary>
/// Turns the user-facing external source (GitHub tree/blob URL, plain repo URL,
/// Azure DevOps URL, or bare .git URL — optionally with the @ref shorthand)
/// into a cloneable repo + ref + subpath.
/// </summary>
public static class ExternalSourceParser
{
    private static readonly Regex GitHubTreeOrBlob = new(
        @"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/(tree|blob)/(?<ref>[^/]+)(/(?<path>.*))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GitHubRepo = new(
        @"^https://github\.com/(?<owner>[^/]+)/(?<repo>[^/]+?)(\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AzureDevOpsRepo = new(
        @"^https://(dev\.azure\.com/(?<org>[^/]+)|(?<org>[^./]+)\.visualstudio\.com)/(?<project>[^/]+)/_git/(?<repo>[^/?#]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ResolvedExternalSource Resolve(AssetSource.External source)
    {
        var tree = GitHubTreeOrBlob.Match(source.Url);
        if (tree.Success)
        {
            var repo = $"https://github.com/{tree.Groups["owner"].Value}/{tree.Groups["repo"].Value}.git";
            var path = source.Path ?? tree.Groups["path"].Value;
            return new ResolvedExternalSource(repo, source.Ref, path, source.Checksum);
        }

        var githubRepo = GitHubRepo.Match(source.Url);
        if (githubRepo.Success)
        {
            var repo = $"https://github.com/{githubRepo.Groups["owner"].Value}/{githubRepo.Groups["repo"].Value}.git";
            return new ResolvedExternalSource(repo, source.Ref, source.Path ?? "", source.Checksum);
        }

        var azure = AzureDevOpsRepo.Match(source.Url);
        if (azure.Success)
        {
            var repo = source.Url[..(azure.Index + azure.Length)];
            var path = source.Path ?? AzurePathFromQuery(source.Url);
            return new ResolvedExternalSource(repo, source.Ref, path, source.Checksum);
        }

        if (source.Url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            return new ResolvedExternalSource(source.Url, source.Ref, source.Path ?? "", source.Checksum);
        }

        throw new AgentPackException(
            $"Unsupported external source URL '{source.Url}'.",
            "Use a GitHub repo/tree URL, an Azure DevOps _git URL, or a plain .git URL.");
    }

    /// <summary>
    /// Splits the <c>url@ref</c> command-line shorthand. Returns the URL and the ref
    /// (ref may come from a GitHub tree URL when no @ref is given).
    /// </summary>
    public static (string Url, string? Ref) SplitShorthand(string value)
    {
        var text = value.Trim();
        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        var at = text.LastIndexOf('@');
        if (at > schemeEnd + 3)
        {
            return (text[..at], text[(at + 1)..]);
        }

        var tree = GitHubTreeOrBlob.Match(text);
        if (tree.Success)
        {
            var reference = tree.Groups["ref"].Value;
            return (text, CatalogValidator.IsPinnedExternalRef(reference) ? reference : null);
        }

        return (text, null);
    }

    private static string AzurePathFromQuery(string url)
    {
        var queryStart = url.IndexOf('?');
        if (queryStart < 0) return "";
        foreach (var pair in url[(queryStart + 1)..].Split('&'))
        {
            if (pair.StartsWith("path=", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair["path=".Length..]).TrimStart('/');
            }
        }

        return "";
    }
}
