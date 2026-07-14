using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using AgentPack.Core.Primitives;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

public sealed class NewCommand : Command<NewCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<kind>")]
        [Description("Asset kind: agents, skills, hooks, mcp, instructions, rules, prompts, templates.")]
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

        [CommandOption("--tool <TOOL>")]
        [Description("Portable agent tool: read, search, edit, execute, web, agent (repeatable).")]
        public string[] Tools { get; set; } = [];

        [CommandOption("--instruction <ID>")]
        public string[] Instructions { get; set; } = [];

        [CommandOption("--skill <ID>")]
        public string[] Skills { get; set; } = [];

        [CommandOption("--mcp <ID>")]
        public string[] Mcp { get; set; } = [];
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

        var name = settings.Name ?? Scaffolder.ToTitle(id);
        var description = settings.Description ?? $"Describe when to use {name}.";

        var providers = settings.Providers
            .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(ProviderNames.Parse)
            .Distinct()
            .ToList();
        var agentAuthoring = AgentAuthoring.From(
            kind, settings.Tools, settings.Instructions, settings.Skills, settings.Mcp);
        if (kind == AssetKind.Agents && Output.CanPrompt)
        {
            if (settings.Name is null) name = Prompts.Text("Agent display name", name);
            if (settings.Description is null) description = Prompts.Text("When should this agent be used?", description);
            (providers, agentAuthoring) = AgentAuthoringWizard.Configure(
                id, name, description, providers, agentAuthoring!,
                promptProviders: settings.Providers.Length == 0,
                promptTools: settings.Tools.Length == 0,
                external: false,
                suggestedTools: null);
            if (!Prompts.Confirm("Create this agent asset?"))
            {
                Output.Info("Nothing created.");
                return ExitCodes.Ok;
            }
        }
        var contentRoot = Path.Combine(assetRoot, "content");
        Directory.CreateDirectory(contentRoot);
        Scaffolder.WriteDefaultContent(kind, id, name, contentRoot);

        File.WriteAllText(manifest, Scaffolder.Manifest(
            kind, name,
            description,
            settings.Groups.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList(),
            providers,
            settings.Owner,
            externalSource: null,
            agentAuthoring));

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

        [CommandOption("--name <NAME>")]
        public string? Name { get; set; }

        [CommandOption("--description <TEXT>")]
        public string? Description { get; set; }

        [CommandOption("--owner <TEAM>")]
        public string? Owner { get; set; }

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

        [CommandOption("--tool <TOOL>")]
        public string[] Tools { get; set; } = [];

        [CommandOption("--instruction <ID>")]
        public string[] Instructions { get; set; } = [];

        [CommandOption("--skill <ID>")]
        public string[] Skills { get; set; } = [];

        [CommandOption("--mcp <ID>")]
        public string[] Mcp { get; set; } = [];

        [CommandOption("--hook-trigger <TRIGGER>")]
        public string? HookTrigger { get; set; }

        [CommandOption("--hook-tool <TOOL>")]
        public string? HookTool { get; set; }

        [CommandOption("--hook-command <COMMAND>")]
        public string? HookCommand { get; set; }

        [CommandOption("--hook-timeout <SECONDS>")]
        public int HookTimeout { get; set; } = 30;

        [CommandOption("--mcp-server <NAME>")]
        public string? McpServer { get; set; }

        [CommandOption("--mcp-transport <TRANSPORT>")]
        public string? McpTransport { get; set; }

        [CommandOption("--mcp-command <COMMAND>")]
        public string? McpCommand { get; set; }

        [CommandOption("--mcp-url <URL>")]
        public string? McpUrl { get; set; }

        [CommandOption("--mcp-arg <ARG>")]
        public string[] McpArgs { get; set; } = [];

        [CommandOption("--mcp-env <NAME>")]
        public string[] McpEnv { get; set; } = [];

        [CommandOption("--mcp-header-env <HEADER=NAME>")]
        public string[] McpHeaderEnv { get; set; } = [];

        [CommandOption("--mcp-tool <TOOL>")]
        public string[] McpTools { get; set; } = [];

        [CommandOption("--mcp-cwd <PATH>")]
        public string? McpCwd { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var kind = AssetKinds.Parse(settings.Kind);
        if (kind is AssetKind.Tools or AssetKind.Templates)
        {
            throw new AgentPackException(
                $"External {kind.Display()} assets are not supported because no provider-native installation contract exists.",
                kind == AssetKind.Tools
                    ? "Package custom tools as an MCP asset with an explicit tool inventory."
                    : "Use a supported asset kind: agents, skills, hooks, mcp, instructions, rules, or prompts.");
        }
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

        var inspection = kind == AssetKind.Agents && Output.CanPrompt
            ? InspectExternalAgent(id, url, reference, settings.License)
            : null;

        var providers = settings.Providers
            .SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(ProviderNames.Parse)
            .Distinct()
            .ToList();
        var kindAuthoring = ExternalKindAuthoring.From(
            kind, id,
            settings.HookTrigger, settings.HookTool, settings.HookCommand, settings.HookTimeout,
            settings.McpServer, settings.McpTransport, settings.McpCommand, settings.McpUrl,
            settings.McpArgs, settings.McpEnv, settings.McpHeaderEnv, settings.McpTools, settings.McpCwd);
        var name = settings.Name ?? inspection?.Name ?? Scaffolder.ToTitle(id);
        var description = settings.Description ?? (kind == AssetKind.Agents
            ? inspection?.Description ?? "Imported external agent — describe exactly when to use it before merging."
            : "Imported external asset — describe when to use it before merging.");
        var agentAuthoring = AgentAuthoring.From(
            kind, settings.Tools, settings.Instructions, settings.Skills, settings.Mcp);
        if (kind == AssetKind.Agents && Output.CanPrompt)
        {
            Output.Warning("Upstream agent frontmatter is untrusted and is not copied automatically. Use the reviewed fields as suggestions only.");
            if (inspection is not null) Output.ExternalAgentInspection(inspection);
            if (settings.Name is null) name = Prompts.Text("Agent display name", name);
            if (settings.Description is null) description = Prompts.Text("When should this agent be used?", description);
            (providers, agentAuthoring) = AgentAuthoringWizard.Configure(
                id, name, description, providers, agentAuthoring!,
                promptProviders: settings.Providers.Length == 0,
                promptTools: settings.Tools.Length == 0,
                external: true,
                suggestedTools: inspection?.SuggestedTools);
            if (!Prompts.Confirm("Create this pinned external agent asset?"))
            {
                Output.Info("Nothing created.");
                return ExitCodes.Ok;
            }
        }
        Directory.CreateDirectory(assetRoot);

        File.WriteAllText(manifest, Scaffolder.Manifest(
            kind, name,
            description,
            settings.Groups.SelectMany(x => x.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList(),
            providers,
            owner: settings.Owner,
            externalSource: (url, reference, settings.License),
            agentAuthoring,
            kindAuthoring.Hook,
            kindAuthoring.Mcp));

        Output.Success($"Created {manifest}");
        Output.Info("Review checklist before opening the PR:");
        Output.Info("  1. Read the upstream content at the pinned ref — you are approving that exact code.");
        Output.Info("  2. Fill in the description and groups.");
        Output.Info("  3. Commit on a branch and open a PR. CI verifies the ref and records the checksum in catalog.lock.yaml.");
        return 0;
    }

    private static ExternalAgentInspection? InspectExternalAgent(
        string id,
        string url,
        string reference,
        string? license)
    {
        try
        {
            Output.Info("Inspecting pinned upstream agent frontmatter...");
            var probe = new Asset
            {
                Id = id,
                Name = id,
                Description = "inspection only",
                Kind = AssetKind.Agents,
                Version = SemVersion.Parse("1.0.0"),
                Providers = ProviderNames.All,
                Source = new AssetSource.External(url, reference, null, null, license),
                Agent = new AgentSpec()
            };
            var source = new ExternalResolver(new AgentPackPaths()).ResolveToCache(probe);
            return ExternalAgentFrontmatter.Inspect(source);
        }
        catch (Exception ex)
        {
            Output.Warning("Could not inspect upstream frontmatter: " + ex.Message +
                           ". Continue by choosing portable capabilities manually; models are always inherited.");
            return null;
        }
    }

}

public sealed record ExternalKindAuthoring(HookSpec? Hook, McpServer? Mcp)
{
    public static ExternalKindAuthoring From(
        AssetKind kind,
        string id,
        string? hookTrigger,
        string? hookTool,
        string? hookCommand,
        int hookTimeout,
        string? mcpServer,
        string? mcpTransport,
        string? mcpCommand,
        string? mcpUrl,
        IReadOnlyList<string> mcpArgs,
        IReadOnlyList<string> mcpEnv,
        IReadOnlyList<string> mcpHeaderEnv,
        IReadOnlyList<string> mcpTools,
        string? mcpCwd)
    {
        var hasHookFlags = hookTrigger is not null || hookTool is not null || hookCommand is not null || hookTimeout != 30;
        var hasMcpFlags = mcpServer is not null || mcpTransport is not null || mcpCommand is not null ||
                          mcpUrl is not null || mcpArgs.Count > 0 || mcpEnv.Count > 0 ||
                          mcpHeaderEnv.Count > 0 || mcpTools.Count > 0 || mcpCwd is not null;

        if (hasHookFlags && kind != AssetKind.Hooks)
            throw new AgentPackException("Hook authoring flags can only be used with --kind hooks.");
        if (hasMcpFlags && kind != AssetKind.Mcp)
            throw new AgentPackException("MCP server authoring flags can only be used with --kind mcp.");

        HookSpec? hook = null;
        if (kind == AssetKind.Hooks)
        {
            if (hookTimeout <= 0) throw new AgentPackException("--hook-timeout must be positive.");
            hook = new HookSpec
            {
                Trigger = EnumParsers.ParseTrigger(hookTrigger, "--hook-trigger"),
                Tool = string.IsNullOrWhiteSpace(hookTool) ? "Bash" : hookTool.Trim(),
                Command = string.IsNullOrWhiteSpace(hookCommand) ? "hook.sh" : hookCommand.Trim(),
                TimeoutSec = hookTimeout
            };
        }

        McpServer? mcp = null;
        if (kind == AssetKind.Mcp && hasMcpFlags)
        {
            var transport = EnumParsers.ParseTransport(mcpTransport, "--mcp-transport");
            if (transport == AgentPack.Core.McpTransport.Stdio && string.IsNullOrWhiteSpace(mcpCommand))
                throw new AgentPackException("A typed stdio MCP import requires --mcp-command.");
            if (transport != AgentPack.Core.McpTransport.Stdio && string.IsNullOrWhiteSpace(mcpUrl))
                throw new AgentPackException("A typed HTTP/SSE MCP import requires --mcp-url.");
            if (transport == AgentPack.Core.McpTransport.Stdio && mcpHeaderEnv.Count > 0)
                throw new AgentPackException("--mcp-header-env is only valid for HTTP/SSE MCP imports.");
            if (transport != AgentPack.Core.McpTransport.Stdio && mcpEnv.Count > 0)
                throw new AgentPackException("--mcp-env is only valid for stdio MCP imports; use --mcp-header-env for HTTP/SSE authentication.");

            mcp = new McpServer
            {
                Server = string.IsNullOrWhiteSpace(mcpServer) ? id : mcpServer.Trim(),
                Transport = transport,
                Command = NullIfWhiteSpace(mcpCommand),
                Url = NullIfWhiteSpace(mcpUrl),
                Args = mcpArgs,
                EnvVars = mcpEnv,
                HeaderEnvVars = ParsePairs(mcpHeaderEnv, "--mcp-header-env"),
                Tools = mcpTools,
                Cwd = NullIfWhiteSpace(mcpCwd)
            };
        }

        return new ExternalKindAuthoring(hook, mcp);
    }

    private static IReadOnlyDictionary<string, string> ParsePairs(IReadOnlyList<string> values, string option)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var separator = value.IndexOf('=');
            if (separator <= 0 || separator == value.Length - 1)
                throw new AgentPackException($"Invalid {option} value '{value}'.", $"Use {option} <name>=<value>.");
            if (!result.TryAdd(value[..separator].Trim(), value[(separator + 1)..].Trim()))
                throw new AgentPackException($"Duplicate {option} name '{value[..separator].Trim()}'.");
        }
        return result;
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record AgentAuthoring(
    IReadOnlyList<AgentTool> Tools,
    IReadOnlyList<string> Instructions,
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Mcp)
{
    public static AgentAuthoring? From(
        AssetKind kind,
        IReadOnlyList<string> tools,
        IReadOnlyList<string> instructions,
        IReadOnlyList<string> skills,
        IReadOnlyList<string> mcp) => kind == AssetKind.Agents
        ? new AgentAuthoring(
            tools.Select(x => EnumParsers.ParseAgentTool(x, "--tool")).Distinct().ToList(),
            instructions, skills, mcp)
        : tools.Count + instructions.Count + skills.Count + mcp.Count == 0
            ? null
            : throw new AgentPackException("Agent dependency flags can only be used with kind 'agents'.");
}

public static class AgentAuthoringWizard
{
    public static (List<ProviderName> Providers, AgentAuthoring Authoring) Configure(
        string id,
        string name,
        string description,
        IReadOnlyList<ProviderName> providers,
        AgentAuthoring authoring,
        bool promptProviders,
        bool promptTools,
        bool external,
        IReadOnlyList<AgentTool>? suggestedTools)
    {
        var selectedProviders = promptProviders
            ? Prompts.SelectAgentProviders(providers.Count > 0 ? providers : null).ToList()
            : providers.ToList();
        if (selectedProviders.Count == 0)
            throw new AgentPackException("An agent must target at least one provider.");

        if (promptTools)
        {
            if (external)
                Output.Info("Choose capabilities matching the reviewed upstream tools. Inherit means the provider's current tool surface; no upstream permission is trusted implicitly.");
            authoring = authoring with
            {
                Tools = Prompts.SelectAgentTools(authoring.Tools.Count > 0 ? authoring.Tools : suggestedTools)
            };
        }

        Output.Warning("Model fields are always removed. The generated agent uses each user's, session's, or workflow's current model.");

        var candidate = new Asset
        {
            Id = id,
            Name = name,
            Description = description,
            Kind = AssetKind.Agents,
            Version = SemVersion.Parse("1.0.0"),
            Providers = selectedProviders,
            Source = new AssetSource.Local("content", null),
            Agent = new AgentSpec
            {
                Tools = authoring.Tools.Count == 0 ? null : authoring.Tools
            }
        };
        Output.AgentCompatibility(candidate, selectedProviders);
        return (selectedProviders, authoring);
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
        (string Url, string Ref, string? License)? externalSource,
        AgentAuthoring? agent = null,
        HookSpec? hook = null,
        McpServer? mcp = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Asset manifest. Id and kind come from the folder path (assets/<kind>/<id>/).");
        builder.AppendLine($"name: {JsonSerializer.Serialize(name)}");
        builder.AppendLine("version: 1.0.0");
        builder.AppendLine($"description: {JsonSerializer.Serialize(description)}");
        if (groups.Count > 0) builder.AppendLine($"groups: [{string.Join(", ", groups)}]");
        builder.AppendLine(providers.Count > 0 && providers.Count < ProviderNames.All.Count
            ? $"providers: [{string.Join(", ", providers.Select(ProviderNames.Display))}]"
            : "# providers omitted = available for all providers");
        if (owner is not null) builder.AppendLine($"owner: {owner}");
        builder.AppendLine("status: experimental");
        builder.AppendLine("channel: internal");

        if (kind == AssetKind.Agents)
        {
            agent ??= new AgentAuthoring([], [], [], []);
            builder.AppendLine("agent:");
            if (agent.Tools.Count > 0)
                builder.AppendLine($"  tools: [{string.Join(", ", agent.Tools.Select(x => x.ToString().ToLowerInvariant()))}]");
            builder.AppendLine("  imports:");
            if (agent.Instructions.Count > 0)
                builder.AppendLine($"    instructions: [{string.Join(", ", agent.Instructions)}]");
            if (agent.Skills.Count > 0)
                builder.AppendLine($"    skills: [{string.Join(", ", agent.Skills)}]");
            if (agent.Mcp.Count > 0)
                builder.AppendLine($"    mcp: [{string.Join(", ", agent.Mcp)}]");
        }

        if (kind == AssetKind.Hooks)
        {
            hook ??= new HookSpec { Tool = "Bash", Command = "hook.sh" };
            builder.AppendLine("hook:");
            builder.AppendLine($"  trigger: {EnumParsers.CamelCase(hook.Trigger.ToString())}");
            if (hook.Tool is not null) builder.AppendLine($"  tool: {JsonSerializer.Serialize(hook.Tool)}");
            if (hook.Command is not null) builder.AppendLine($"  command: {JsonSerializer.Serialize(hook.Command)}");
            builder.AppendLine($"  timeoutSec: {hook.TimeoutSec}");
        }

        if (kind == AssetKind.Mcp && (mcp is not null || externalSource is null))
        {
            mcp ??= new McpServer { Server = "replace-me", Command = "replace-me" };
            builder.AppendLine("mcp:");
            builder.AppendLine($"  server: {JsonSerializer.Serialize(mcp.Server)}");
            builder.AppendLine($"  transport: {mcp.Transport.ToString().ToLowerInvariant()}");
            if (mcp.Command is not null) builder.AppendLine($"  command: {JsonSerializer.Serialize(mcp.Command)}");
            if (mcp.Url is not null) builder.AppendLine($"  url: {JsonSerializer.Serialize(mcp.Url)}");
            if (mcp.Args.Count > 0) builder.AppendLine($"  args: [{string.Join(", ", mcp.Args.Select(x => JsonSerializer.Serialize(x)))}]");
            if (mcp.EnvVars.Count > 0) builder.AppendLine($"  envVars: [{string.Join(", ", mcp.EnvVars.Select(x => JsonSerializer.Serialize(x)))}]");
            if (mcp.Tools.Count > 0) builder.AppendLine($"  tools: [{string.Join(", ", mcp.Tools.Select(x => JsonSerializer.Serialize(x)))}]");
            if (mcp.Cwd is not null) builder.AppendLine($"  cwd: {JsonSerializer.Serialize(mcp.Cwd)}");
            if (mcp.HeaderEnvVars.Count > 0)
            {
                builder.AppendLine("  headerEnvVars:");
                foreach (var (header, envVar) in mcp.HeaderEnvVars.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
                    builder.AppendLine($"    {JsonSerializer.Serialize(header)}: {JsonSerializer.Serialize(envVar)}");
            }
        }

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

        return builder.ToString();
    }

    public static void WriteDefaultContent(AssetKind kind, string id, string name, string contentRoot)
    {
        switch (kind)
        {
            case AssetKind.Agents:
                File.WriteAllText(Path.Combine(contentRoot, "AGENT.md"), $"# {name}\n\nDescribe the agent's role, workflow, boundaries, and expected output.\n");
                break;

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
