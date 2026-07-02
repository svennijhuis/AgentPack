using System.ComponentModel;
using System.Text;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

public sealed class NewCommand : Command<NewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<kind>")]
        [Description("Asset kind: skills, hooks, mcp, tools, instructions, rules, prompts, templates.")]
        public string Kind { get; set; } = "";

        [CommandArgument(1, "<id>")]
        [Description("Kebab-case asset id — also the folder name.")]
        public string Id { get; set; } = "";

        [CommandOption("--name <NAME>")]
        public string? Name { get; set; }

        [CommandOption("--description <TEXT>")]
        public string? Description { get; set; }

        [CommandOption("-g|--group <GROUP>")]
        [Description("Groups this asset belongs to (repeatable or comma-separated).")]
        public string[] Groups { get; set; } = [];

        [CommandOption("-p|--provider <PROVIDER>")]
        [Description("Limit to specific providers. Omit for all providers (the default).")]
        public string[] Providers { get; set; } = [];

        [CommandOption("--owner <TEAM>")]
        [Description("Owning team (optional — CODEOWNERS usually covers this).")]
        public string? Owner { get; set; }

        [CommandOption("--force")]
        [Description("Overwrite an existing manifest.")]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var kind = AssetKinds.Parse(settings.Kind);
        var id = settings.Id.Trim().ToLowerInvariant();

        var assetRoot = Path.Combine("assets", kind.Display(), id);
        var manifest = Path.Combine(assetRoot, "agentpack.yaml");
        if (File.Exists(manifest) && !settings.Force)
        {
            throw new AgentPackException(
                $"Asset '{id}' already exists at {manifest}.",
                "Use --force to overwrite it.");
        }

        var contentRoot = Path.Combine(assetRoot, "content");
        Directory.CreateDirectory(contentRoot);
        var name = settings.Name ?? Scaffolder.ToTitle(id);
        Scaffolder.WriteDefaultContent(kind, id, name, contentRoot);

        var providers = settings.Providers
            .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(ProviderNames.Parse)
            .Distinct()
            .ToList();

        File.WriteAllText(manifest, Scaffolder.Manifest(
            kind, name,
            settings.Description ?? $"Describe when to use {name}.",
            settings.Groups.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList(),
            providers,
            settings.Owner,
            externalSource: null));

        Output.Success($"Created {manifest}");
        Output.Success($"Created content in {contentRoot}");
        Output.Info("Next: edit the content, commit on a branch, and open a PR. CI runs 'agentpack catalog validate' and 'agentpack catalog lock'.");
        return 0;
    }
}

public sealed class ImportCommand : Command<ImportCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<url>")]
        [Description("Upstream URL, ideally with the pinned ref: https://github.com/owner/repo/tree/main/path@<commit-sha>.")]
        public string Url { get; set; } = "";

        [CommandOption("--ref <REF>")]
        [Description("Reviewed commit SHA or immutable tag (alternative to the @ref suffix).")]
        public string? Ref { get; set; }

        [CommandOption("--kind <KIND>")]
        [Description("Asset kind. Default: skills.")]
        public string Kind { get; set; } = "skills";

        [CommandOption("--id <ID>")]
        [Description("Asset id. Default: last URL path segment.")]
        public string? Id { get; set; }

        [CommandOption("-g|--group <GROUP>")]
        public string[] Groups { get; set; } = [];

        [CommandOption("-p|--provider <PROVIDER>")]
        [Description("Limit to specific providers. Omit for all providers (the default).")]
        public string[] Providers { get; set; } = [];

        [CommandOption("--license <LICENSE>")]
        [Description("Upstream license (e.g. MIT). Recorded for compliance.")]
        public string? License { get; set; }

        [CommandOption("--force")]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var kind = AssetKinds.Parse(settings.Kind);
        var (url, shorthandRef) = ExternalSourceParser.SplitShorthand(settings.Url);
        var reference = settings.Ref ?? shorthandRef;
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new AgentPackException(
                "External assets must pin the reviewed upstream commit.",
                "Append '@<commit-sha>' to the URL, or pass --ref <commit-sha>.");
        }

        if (!CatalogValidator.IsPinnedExternalRef(reference))
        {
            throw new AgentPackException(
                $"'{reference}' is a moving ref (branch). External assets must pin a commit SHA or immutable tag.",
                "Use the full 40-character commit SHA you reviewed.");
        }

        var id = (settings.Id ?? Path.GetFileName(url.TrimEnd('/'))).ToLowerInvariant();
        var assetRoot = Path.Combine("assets", kind.Display(), id);
        var manifest = Path.Combine(assetRoot, "agentpack.yaml");
        if (File.Exists(manifest) && !settings.Force)
        {
            throw new AgentPackException($"Asset '{id}' already exists at {manifest}.", "Use --force to overwrite it.");
        }

        Directory.CreateDirectory(assetRoot);
        var providers = settings.Providers
            .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(ProviderNames.Parse)
            .Distinct()
            .ToList();

        File.WriteAllText(manifest, Scaffolder.Manifest(
            kind, Scaffolder.ToTitle(id),
            "Imported external asset — describe when to use it before merging.",
            settings.Groups.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList(),
            providers,
            owner: null,
            externalSource: (url, reference, settings.License)));

        Output.Success($"Created {manifest}");
        Output.Info("Review checklist before opening the PR:");
        Output.Info("  1. Read the upstream content at the pinned ref — you are approving that exact code.");
        Output.Info("  2. Fill in the description and groups.");
        Output.Info("  3. Commit on a branch and open a PR. CI verifies the ref and records the checksum in catalog.lock.yaml.");
        return 0;
    }
}

public static class Scaffolder
{
    public static string Manifest(
        AssetKind kind,
        string name,
        string description,
        IReadOnlyList<string> groups,
        IReadOnlyList<ProviderName> providers,
        string? owner,
        (string Url, string Ref, string? License)? externalSource)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Asset manifest. Id and kind come from the folder path (assets/<kind>/<id>/).");
        builder.AppendLine($"name: {name}");
        builder.AppendLine("version: 1.0.0");
        builder.AppendLine($"description: {description}");
        if (groups.Count > 0) builder.AppendLine($"groups: [{string.Join(", ", groups)}]");
        builder.AppendLine(providers.Count > 0 && providers.Count < ProviderNames.All.Count
            ? $"providers: [{string.Join(", ", providers.Select(ProviderNames.Display))}]"
            : "# providers omitted = available for all providers");
        if (owner is not null) builder.AppendLine($"owner: {owner}");
        builder.AppendLine("status: experimental");
        builder.AppendLine("channel: internal");

        if (externalSource is { } external)
        {
            if (external.License is null)
            {
                builder.AppendLine($"source: {external.Url}@{external.Ref}");
            }
            else
            {
                // The one-line shorthand cannot carry a license; use the mapping form.
                builder.AppendLine("source:");
                builder.AppendLine($"  url: {external.Url}");
                builder.AppendLine($"  ref: {external.Ref}");
                builder.AppendLine($"  license: {external.License}");
            }
        }
        else
        {
            switch (kind)
            {
                case AssetKind.Mcp:
                    builder.AppendLine("mcp:");
                    builder.AppendLine("  server: replace-me");
                    builder.AppendLine("  transport: stdio");
                    builder.AppendLine("  command: replace-me");
                    builder.AppendLine("  envVars: []");
                    break;
                case AssetKind.Hooks:
                    builder.AppendLine("hook:");
                    builder.AppendLine("  trigger: preToolUse");
                    builder.AppendLine("  tool: Bash");
                    builder.AppendLine("  command: hook.sh");
                    builder.AppendLine("  timeoutSec: 30");
                    break;
            }
        }

        return builder.ToString();
    }

    public static void WriteDefaultContent(AssetKind kind, string id, string name, string contentRoot)
    {
        switch (kind)
        {
            case AssetKind.Skills:
                File.WriteAllText(Path.Combine(contentRoot, "SKILL.md"), $"""
                ---
                name: {id}
                description: Describe exactly when this skill should be used.
                ---

                # {name}

                Use this skill when...

                ## Steps

                1. Clarify the goal.
                2. Inspect the relevant files or context.
                3. Produce the requested output with clear assumptions.
                """);
                Directory.CreateDirectory(Path.Combine(contentRoot, "references"));
                File.WriteAllText(Path.Combine(contentRoot, "references", "README.md"), "# References\n\nAdd supporting reference material here.\n");
                break;

            case AssetKind.Hooks:
                var hookPath = Path.Combine(contentRoot, "hook.sh");
                File.WriteAllText(hookPath, "#!/usr/bin/env bash\nset -euo pipefail\ncat >/dev/null\necho '{\"ok\":true}'\n");
                ContentHash.MakeExecutable(hookPath);
                break;

            case AssetKind.Mcp:
                File.WriteAllText(Path.Combine(contentRoot, "mcp.json"),
                    "{\n  \"name\": \"" + id + "\",\n  \"transport\": \"stdio\",\n  \"command\": \"replace-me\",\n  \"envVars\": []\n}\n");
                break;

            case AssetKind.Rules:
                File.WriteAllText(Path.Combine(contentRoot, id + ".mdc"),
                    "---\ndescription: Describe when this rule applies.\n---\n\n# " + name + "\n\nAdd the rule here.\n");
                break;

            case AssetKind.Instructions:
            case AssetKind.Prompts:
                File.WriteAllText(Path.Combine(contentRoot, id + ".md"), "# " + name + "\n\nAdd the content here.\n");
                break;

            default:
                File.WriteAllText(Path.Combine(contentRoot, "README.md"), "# " + name + "\n");
                break;
        }
    }

    public static string ToTitle(string id) =>
        string.Join(' ', id.Split('-', '_').Where(x => x.Length > 0).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
}
