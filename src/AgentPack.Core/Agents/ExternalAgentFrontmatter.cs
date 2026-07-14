using System.Collections;

namespace AgentPack.Core;

/// <summary>
/// Reads upstream agent frontmatter for authoring suggestions only. The result
/// never becomes trusted configuration until an author selects it into a manifest.
/// </summary>
public static class ExternalAgentFrontmatter
{
    public static ExternalAgentInspection? Inspect(string sourcePath)
    {
        var path = FindMarkdown(sourcePath);
        if (path is null) return null;
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) return null;
        var end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0) return null;

        var values = CatalogLoader.FromYaml<Dictionary<string, object?>>(text[4..end]);
        var tools = StringList(Value(values, "tools"));
        var mappings = tools.Select(MapTool).ToList();
        return new ExternalAgentInspection(
            String(Value(values, "name")),
            String(Value(values, "description")),
            String(Value(values, "model")),
            tools,
            mappings);
    }

    private static ExternalToolMapping MapTool(string native)
    {
        var normalized = native.Replace("-", "").Replace("_", "").ToLowerInvariant();
        var portable = normalized switch
        {
            "codebase" => new[] { AgentTool.Read, AgentTool.Search },
            "read" or "view" or "notebookread" => [AgentTool.Read],
            "search" or "grep" or "glob" => [AgentTool.Search],
            "edit" or "write" or "multiedit" or "notebookedit" => [AgentTool.Edit],
            "terminalcommand" or "execute" or "shell" or "bash" or "powershell" => [AgentTool.Execute],
            "web" or "webfetch" or "websearch" => [AgentTool.Web],
            "agent" or "task" or "customagent" => [AgentTool.Agent],
            _ => []
        };
        return new ExternalToolMapping(native, portable);
    }

    private static object? Value(IReadOnlyDictionary<string, object?> values, string name) =>
        values.FirstOrDefault(x => x.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? String(object? value) => string.IsNullOrWhiteSpace(value?.ToString()) ? null : value.ToString();

    private static IReadOnlyList<string> StringList(object? value)
    {
        if (value is null) return [];
        if (value is string scalar)
            return scalar.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (value is IEnumerable sequence)
            return sequence.Cast<object?>().Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList();
        return [value.ToString() ?? ""];
    }

    private static string? FindMarkdown(string sourcePath)
    {
        if (File.Exists(sourcePath)) return sourcePath;
        if (!Directory.Exists(sourcePath)) return null;
        var files = Directory.EnumerateFiles(sourcePath, "*.md", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .ToList();
        return files.FirstOrDefault(x => Path.GetFileName(x).EndsWith(".agent.md", StringComparison.OrdinalIgnoreCase))
               ?? files.FirstOrDefault(x => Path.GetFileName(x).Equals("AGENT.md", StringComparison.OrdinalIgnoreCase))
               ?? (files.Count == 1 ? files[0] : null);
    }
}

public sealed record ExternalAgentInspection(
    string? Name,
    string? Description,
    string? Model,
    IReadOnlyList<string> NativeTools,
    IReadOnlyList<ExternalToolMapping> ToolMappings)
{
    public IReadOnlyList<AgentTool> SuggestedTools => ToolMappings
        .SelectMany(x => x.PortableTools)
        .Distinct()
        .ToList();

    public IReadOnlyList<string> UnknownTools => ToolMappings
        .Where(x => x.PortableTools.Count == 0)
        .Select(x => x.NativeTool)
        .ToList();
}

public sealed record ExternalToolMapping(string NativeTool, IReadOnlyList<AgentTool> PortableTools);
