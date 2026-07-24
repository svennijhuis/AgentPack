using System.Text;
using AgentPack.Core;

namespace AgentPack.Cli.Commands;

/// <summary>Creates the reviewed manifest placed on a submit proposal branch.</summary>
public static class SubmissionScaffolder
{
    public static string Manifest(
        AssetKind kind,
        string name,
        string version,
        string description,
        IReadOnlyList<string> groups,
        IReadOnlyList<ProviderName> providers,
        (string Url, string Ref, string? License)? externalSource,
        SubmittedHook? hook = null,
        SubmittedMcp? mcp = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Asset manifest. Id and kind come from the folder path (assets/<kind>/<id>/).");
        builder.AppendLine($"name: {Scalar(name)}");
        builder.AppendLine($"version: {Scalar(version)}");
        builder.AppendLine($"description: {Scalar(description)}");
        if (groups.Count > 0) builder.AppendLine($"groups: {List(groups)}");
        builder.AppendLine(providers.Count > 0 && providers.Count < ProviderNames.All.Count
            ? $"providers: {List(providers.Select(ProviderNames.Display))}"
            : "# providers omitted = available for all providers");

        if (externalSource is { } external)
        {
            builder.AppendLine("source:");
            builder.AppendLine($"  url: {Scalar(external.Url)}");
            builder.AppendLine($"  ref: {Scalar(external.Ref)}");
            if (external.License is not null) builder.AppendLine($"  license: {Scalar(external.License)}");
        }

        if (mcp is not null)
        {
            builder.AppendLine("mcp:");
            builder.AppendLine($"  server: {Scalar(mcp.Server)}");
            builder.AppendLine($"  transport: {EnumParsers.CamelCase(mcp.Transport.ToString())}");
            if (mcp.Command is not null) builder.AppendLine($"  command: {Scalar(mcp.Command)}");
            if (mcp.Args.Count > 0) builder.AppendLine($"  args: {List(mcp.Args)}");
            if (mcp.EnvVars.Count > 0) builder.AppendLine($"  envVars: {List(mcp.EnvVars)}");
            if (mcp.Url is not null) builder.AppendLine($"  url: {Scalar(mcp.Url)}");
            if (mcp.HeaderEnvVars.Count > 0)
            {
                builder.AppendLine("  headerEnvVars:");
                foreach (var (header, envVar) in mcp.HeaderEnvVars.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                {
                    builder.AppendLine($"    {Scalar(header)}: {Scalar(envVar)}");
                }
            }
        }

        if (hook is not null)
        {
            builder.AppendLine("hook:");
            builder.AppendLine($"  trigger: {EnumParsers.CamelCase(hook.Trigger.ToString())}");
            if (hook.Tool is not null) builder.AppendLine($"  tool: {Scalar(hook.Tool)}");
            builder.AppendLine($"  command: {Scalar(hook.Command)}");
            builder.AppendLine($"  timeoutSec: {hook.TimeoutSec}");
        }

        return builder.ToString();
    }

    public static string ToTitle(string id) =>
        string.Join(' ', id.Split('-', '_').Where(x => x.Length > 0).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));

    private static string List(IEnumerable<string> values) =>
        $"[{string.Join(", ", values.Select(Scalar))}]";

    private static string Scalar(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return $"'{singleLine.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}

public sealed record SubmittedHook(HookTrigger Trigger, string? Tool, string Command, int TimeoutSec);

public sealed record SubmittedMcp(
    string Server,
    McpTransport Transport,
    string? Command,
    IReadOnlyList<string> Args,
    IReadOnlyList<string> EnvVars,
    string? Url,
    IReadOnlyDictionary<string, string> HeaderEnvVars);
