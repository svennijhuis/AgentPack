using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentPack.Core;

/// <summary>The result of merging a fragment into a provider config file.</summary>
public sealed record MergeResult(string Checksum, string Fragment);

/// <summary>Whether a previously installed fragment is still in the provider config.</summary>
public enum FragmentState
{
    Present,
    Modified,
    Absent
}

/// <summary>
/// Parses user-owned provider config files (.claude/settings.json, .mcp.json,
/// .vscode/mcp.json, ...). Tolerates comments and trailing commas — VS Code's
/// mcp.json is JSONC — and turns parse failures into actionable errors instead
/// of an internal-error stack trace.
/// </summary>
public static class UserConfigJson
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Loads the file as a JSON object; a missing or empty file is an empty object.</summary>
    public static JsonObject LoadObject(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();

        try
        {
            return JsonNode.Parse(text, documentOptions: Lenient)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new AgentPackException(
                $"{path} is not valid JSON: {ex.Message}",
                "Fix the file (or move it aside) and rerun; agentpack never overwrites a config it cannot parse.");
        }
        catch (InvalidOperationException)
        {
            throw new AgentPackException(
                $"{path} does not contain a JSON object at the root.",
                "Fix the file (or move it aside) and rerun; agentpack never overwrites a config it cannot parse.");
        }
    }

    /// <summary>Parses a fragment stored in the lockfile. Fragments are written by agentpack, so failures are internal.</summary>
    public static JsonObject ParseFragment(string fragment) =>
        JsonNode.Parse(fragment)?.AsObject() ?? throw new AgentPackException("A lockfile fragment is corrupt.", "Reinstall the affected asset.");
}
