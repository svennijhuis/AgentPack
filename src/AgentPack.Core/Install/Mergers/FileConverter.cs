using System.Text;
using YamlDotNet.RepresentationModel;

namespace AgentPack.Core;

/// <summary>
/// Translates a single content file into the format a provider actually reads.
/// The catalog stores one canonical file per asset; two targets need a dialect:
///
/// - Claude Code rules (.claude/rules/&lt;id&gt;.md): Cursor .mdc frontmatter is
///   rewritten — `globs` becomes `paths`, `alwaysApply: true` drops the scoping
///   so the rule always loads.
/// - Codex agents (.codex/agents/&lt;id&gt;.toml): agent markdown frontmatter and
///   body become the TOML fields `name`, `description`, `developer_instructions`,
///   and optionally `model`.
/// </summary>
public static class FileConverter
{
    public static MergeResult Apply(Asset asset, string sourcePath, InstallTarget target, string targetPath, Action<string> backupIfExists)
    {
        var converted = Convert(asset, target, File.ReadAllText(sourcePath));

        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            backupIfExists(targetPath);
            if (Directory.Exists(targetPath)) Directory.Delete(targetPath, recursive: true);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        AtomicWrite.Text(targetPath, converted);
        return new MergeResult(ContentHash.Compute(targetPath), "");
    }

    /// <summary>The exact text that will be written — also used for previews and tests.</summary>
    public static string Convert(Asset asset, InstallTarget target, string sourceText) => (target.Provider, target.Kind) switch
    {
        (ProviderName.Claude, AssetKind.Rules) => ClaudeRule(sourceText),
        (ProviderName.Codex, AssetKind.Agents) => CodexAgentToml(asset, sourceText),
        _ => throw new AgentPackException($"{target.Provider.Display()} has no file conversion for {target.Kind.Display()}.")
    };

    private static string ClaudeRule(string sourceText)
    {
        var (frontmatter, body) = SplitFrontmatter(sourceText);
        var description = Scalar(frontmatter, "description");
        var alwaysApply = string.Equals(Scalar(frontmatter, "alwaysApply"), "true", StringComparison.OrdinalIgnoreCase);
        var paths = alwaysApply ? [] : StringList(frontmatter, "globs");

        var builder = new StringBuilder();
        if (description is not null || paths.Count > 0)
        {
            builder.AppendLine("---");
            if (description is not null) builder.AppendLine("description: " + YamlScalar(description));
            if (paths.Count > 0)
            {
                builder.AppendLine("paths:");
                foreach (var path in paths) builder.AppendLine("  - " + YamlScalar(path));
            }

            builder.AppendLine("---");
            if (body.Length > 0) builder.AppendLine();
        }

        builder.Append(body);
        return EnsureTrailingNewline(builder.ToString());
    }

    private static string CodexAgentToml(Asset asset, string sourceText)
    {
        var (frontmatter, body) = SplitFrontmatter(sourceText);
        var builder = new StringBuilder();
        builder.AppendLine("name = " + TomlString(Scalar(frontmatter, "name") ?? asset.Id));
        builder.AppendLine("description = " + TomlString(Scalar(frontmatter, "description") ?? asset.Description));
        var model = Scalar(frontmatter, "model");
        if (model is not null) builder.AppendLine("model = " + TomlString(model));

        builder.AppendLine("developer_instructions = \"\"\"");
        // Multi-line basic strings process escapes: backslashes must be escaped,
        // and an embedded """ must not terminate the string.
        builder.Append(EnsureTrailingNewline(body.Replace("\\", "\\\\").Replace("\"\"\"", "\"\"\\\"")));
        builder.AppendLine("\"\"\"");
        return builder.ToString();
    }

    /// <summary>Splits a leading YAML frontmatter block (--- ... ---) from the body.</summary>
    private static (YamlMappingNode? Frontmatter, string Body) SplitFrontmatter(string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return (null, normalized.TrimStart('\n'));

        var end = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0) return (null, normalized.TrimStart('\n'));

        var yamlText = normalized[4..(end + 1)];
        var bodyStart = normalized.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? "" : normalized[(bodyStart + 1)..].TrimStart('\n');

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yamlText));
            return (stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null, body);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new AgentPackException(
                $"The content file has invalid YAML frontmatter: {ex.Message}",
                "Fix the frontmatter block between the --- markers.");
        }
    }

    private static string? Scalar(YamlMappingNode? map, string key)
    {
        if (map is null) return null;
        var value = map.Children
            .FirstOrDefault(x => x.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            .Value;
        return value is YamlScalarNode { Value: { Length: > 0 } text } ? text : null;
    }

    /// <summary>A frontmatter list value: a YAML sequence, or a comma-separated scalar (.mdc allows both).</summary>
    private static List<string> StringList(YamlMappingNode? map, string key)
    {
        if (map is null) return [];
        var value = map.Children
            .FirstOrDefault(x => x.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            .Value;

        return value switch
        {
            YamlSequenceNode sequence => sequence.Children.OfType<YamlScalarNode>()
                .Select(x => x.Value?.Trim() ?? "").Where(x => x.Length > 0).ToList(),
            YamlScalarNode { Value: { Length: > 0 } text } => text.Split(',')
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList(),
            _ => []
        };
    }

    /// <summary>Globs start with YAML-special characters (*.ts), so quote whenever in doubt.</summary>
    private static string YamlScalar(string value)
    {
        var safe = value.Length > 0 &&
                   char.IsLetterOrDigit(value[0]) &&
                   !value.Contains(':') && !value.Contains('#') &&
                   value.Trim().Length == value.Length;
        return safe ? value : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string TomlString(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static string EnsureTrailingNewline(string text) =>
        text.Length == 0 || text.EndsWith('\n') ? text : text + "\n";
}
