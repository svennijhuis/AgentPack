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

    private static readonly Regex GitHubSshRepo = new(
        @"^(ssh://git@github\.com/|git@github\.com:)(?<owner>[^/]+)/(?<repo>[^/]+?)(\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The cloneable repo URL for a source URL, plus the branch the URL names when it
    /// is a tree/blob link. Used to pin a submission to a commit before that submission
    /// has a manifest to <see cref="Resolve"/>.
    /// </summary>
    public static (string Repo, string? Branch) RepositoryAndBranch(string url)
    {
        var tree = GitHubTreeOrBlob.Match(url);
        if (tree.Success)
        {
            return ($"https://github.com/{tree.Groups["owner"].Value}/{tree.Groups["repo"].Value}.git",
                tree.Groups["ref"].Value);
        }

        var githubRepo = GitHubRepo.Match(url);
        if (githubRepo.Success)
        {
            return ($"https://github.com/{githubRepo.Groups["owner"].Value}/{githubRepo.Groups["repo"].Value}.git", null);
        }

        var azure = AzureDevOpsRepo.Match(url);
        if (azure.Success) return (url[..(azure.Index + azure.Length)], null);

        return (url, null);
    }

    /// <summary>
    /// The <c>owner/repo</c> slug when the URL points at GitHub over HTTPS or SSH,
    /// otherwise null. Only GitHub-hosted catalogs can be driven by the 'gh' CLI.
    /// </summary>
    public static string? GitHubSlug(string url)
    {
        var text = url.Trim();
        var match = GitHubTreeOrBlob.Match(text);
        if (!match.Success) match = GitHubRepo.Match(text);
        if (!match.Success) match = GitHubSshRepo.Match(text);
        return match.Success ? $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}" : null;
    }

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

    /// <summary>Compact repository identity used as the visible attribution in catalog listings.</summary>
    public static string RepositoryLabel(AssetSource.External source)
    {
        var tree = GitHubTreeOrBlob.Match(source.Url);
        if (tree.Success) return $"{tree.Groups["owner"].Value}/{tree.Groups["repo"].Value}";

        var githubRepo = GitHubRepo.Match(source.Url);
        if (githubRepo.Success) return $"{githubRepo.Groups["owner"].Value}/{githubRepo.Groups["repo"].Value}";

        var azure = AzureDevOpsRepo.Match(source.Url);
        if (azure.Success) return $"{azure.Groups["org"].Value}/{azure.Groups["project"].Value}/{azure.Groups["repo"].Value}";

        return source.Url;
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
