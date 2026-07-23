using System.ComponentModel;
using System.Text.RegularExpressions;
using AgentPack.Cli.Ui;
using AgentPack.Core;
using AgentPack.Core.Primitives;
using Spectre.Console.Cli;

namespace AgentPack.Cli.Commands;

/// <summary>
/// Turns a local folder or pinned external repository path into a reviewed catalog
/// contribution. The command never writes to the catalog's default branch.
/// </summary>
public sealed class SubmitCommand : Command<SubmitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<kind>")]
        [Description("Asset kind, for example skill, hook, mcp, prompt, agent, or tool.")]
        public string Kind { get; set; } = "";

        [CommandArgument(1, "<path-or-url-or-id>")]
        [Description("Local file/folder, external git URL, or the id of an MCP server.")]
        public string Source { get; set; } = "";

        [CommandOption("--id <ID>")]
        [Description("Catalog id. Default: derived from the file, folder, or URL.")]
        public string? Id { get; set; }

        [CommandOption("--name <NAME>")]
        [Description("Display name. Default: the id in title case.")]
        public string? Name { get; set; }

        [CommandOption("--description <TEXT>")]
        [Description("One line telling a reader when to use this asset.")]
        public string? Description { get; set; }

        [CommandOption("--version <VERSION>")]
        [Description("Catalog asset version. Default: 1.0.0 for a new asset, the next patch when updating.")]
        public string? Version { get; set; }

        [CommandOption("--ref <REF>")]
        [Description("Specific external commit or immutable tag. Omit to pin the latest current commit.")]
        public string? Ref { get; set; }

        [CommandOption("-g|--group <GROUP>")]
        [Description("Catalog groups this asset belongs to (repeatable or comma-separated).")]
        public string[] Groups { get; set; } = [];

        [CommandOption("-p|--provider <PROVIDER>")]
        [Description("Limit to specific providers. Omit for all providers (the default).")]
        public string[] Providers { get; set; } = [];

        [CommandOption("--license <LICENSE>")]
        [Description("SPDX license identifier for an external asset, when known.")]
        public string? License { get; set; }

        [CommandOption("--command <COMMAND>")]
        [Description("Hook entry file, or the executable for a stdio MCP server.")]
        public string? Command { get; set; }

        [CommandOption("--trigger <TRIGGER>")]
        [Description("Hook event. Default: preToolUse.")]
        public string? Trigger { get; set; }

        [CommandOption("--tool <MATCHER>")]
        [Description("Optional hook tool matcher, for example Bash.")]
        public string? Tool { get; set; }

        [CommandOption("--timeout <SECONDS>")]
        [Description("Hook timeout in seconds. Default: 30.")]
        public int? Timeout { get; set; }

        [CommandOption("--transport <TRANSPORT>")]
        [Description("MCP transport: stdio, http, or sse. Usually inferred.")]
        public string? Transport { get; set; }

        [CommandOption("--url <URL>")]
        [Description("HTTP/SSE MCP endpoint. Use environment-backed headers for authentication.")]
        public string? McpUrl { get; set; }

        [CommandOption("--arg <ARG>")]
        [Description("MCP process argument. Repeat for multiple arguments.")]
        public string[] Args { get; set; } = [];

        [CommandOption("--env <NAME>")]
        [Description("Required MCP environment variable name. Never pass a secret value.")]
        public string[] EnvVars { get; set; } = [];

        [CommandOption("--header-env <HEADER=ENV>")]
        [Description("MCP HTTP header backed by an environment variable, for example Authorization=API_TOKEN.")]
        public string[] HeaderEnvVars { get; set; } = [];

        [CommandOption("-y|--yes")]
        [Description("Publish the previewed files without an interactive confirmation.")]
        public bool Yes { get; set; }

        [CommandOption("--draft")]
        [Description("Open the pull request as a draft.")]
        public bool Draft { get; set; }

        [CommandOption("--prepare-only")]
        [Description("Create and commit the proposal branch locally, but do not push or open a PR.")]
        public bool PrepareOnly { get; set; }

        [CommandOption("--update")]
        [Description("Propose a new revision of an asset that is already in the catalog.")]
        public bool Update { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var session = new CliSession();
        var kind = AssetKinds.Parse(settings.Kind);
        if (kind is AssetKind.Tools or AssetKind.Templates)
        {
            throw new AgentPackException(
                $"{kind.Display()} cannot be submitted yet because no supported provider can install them safely.",
                "Submit a skill, hook, MCP server, instruction, rule, prompt, or agent instead.");
        }

        // Flag shape is checked before any disk or network work, so a mistyped option
        // fails immediately instead of after a full submission walk.
        ValidateKindOptions(kind, settings);

        var input = kind == AssetKind.Mcp
            ? null
            : ResolveInput(settings.Source, session.Paths.WorkingDirectory);
        var id = NormalizeId(settings.Id ?? (input is null ? settings.Source : SuggestedId(input)));
        var name = settings.Name ?? SubmissionScaffolder.ToTitle(id);

        var local = input is { IsExternal: false }
            ? LocalSubmissionScanner.Scan(input.Value, kind)
            : null;
        var hook = kind == AssetKind.Hooks ? BuildHook(settings, input!, local) : null;
        var mcp = kind == AssetKind.Mcp ? BuildMcp(settings, id) : null;

        var source = session.Sources.RequireEffectiveSource();

        // Resolve add-vs-update against the catalog we already have, so a wrong id fails
        // in a second instead of after a full clone.
        var existing = FindExistingAsset(session, id);
        var version = ResolveVersion(settings, id, existing, settings.Update);
        var plan = new SubmissionPlan(kind, id, name, version, input, local, hook, mcp);

        ShowSubmissionPreview(source, plan, settings.Ref, settings.Update);
        if (!settings.PrepareOnly)
        {
            EnsureGitHubCli(session.Paths.WorkingDirectory);
            if (!settings.Yes)
            {
                if (!Output.CanPrompt)
                {
                    throw new AgentPackException(
                        "Publishing a catalog proposal needs confirmation.",
                        "Review the file preview, then rerun with --yes. Use --prepare-only for a local review branch.");
                }

                if (!Prompts.ConfirmSubmission("Create a branch, push it, and open this catalog pull request?"))
                {
                    Output.Info("Submission cancelled; nothing was cloned, pushed, or changed.");
                    return ExitCodes.Ok;
                }
            }
        }

        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        var branch = $"agentpack/submit/{id}-{stamp}";
        var submission = Path.Combine(session.Paths.SubmissionsRoot, $"{id}-{stamp}");
        Directory.CreateDirectory(session.Paths.SubmissionsRoot);

        // Any failure past this point leaves a clone behind, and on the publish path the
        // clone has no purpose once the PR exists. Only --prepare-only keeps it, because
        // there the checkout is the deliverable.
        var keepCheckout = settings.PrepareOnly;
        try
        {
            return Publish(session, settings, source, plan, branch, submission, ref keepCheckout);
        }
        finally
        {
            if (!keepCheckout) TryDeleteDirectory(submission);
        }
    }

    private int Publish(
        CliSession session,
        Settings settings,
        AgentPackSource source,
        SubmissionPlan plan,
        string branch,
        string submission,
        ref bool keepCheckout)
    {
        var (kind, id, name, version, input, local, hook, mcp) = plan;
        var update = settings.Update;
        // The checkout, not the caller's cwd, is where a failed proposal can be inspected.
        var git = new GitRunner($"The proposal was not merged into main. Working directory: {submission}");

        // Blobless keeps full history (so the branch still pushes) while skipping file
        // contents the proposal never reads.
        git.Run(["clone", "--filter=blob:none", "--branch", source.Branch, "--", source.Url, submission],
            session.Paths.WorkingDirectory, $"Git could not clone catalog '{source.Name}'");
        git.Run(["switch", "-c", branch], submission, $"Git could not create proposal branch '{branch}'");

        var assetRoot = Path.Combine(submission, "assets", kind.Display(), id);
        EnsureMatchesCatalog(id, Directory.Exists(assetRoot), update, existingVersion: null);

        // Replace the whole asset folder so a removed file in the new revision is
        // actually removed by the proposal instead of lingering from the old one.
        if (update) Directory.Delete(assetRoot, recursive: true);

        var groups = CommandHelpers.SplitList(settings.Groups)
            .ToList();
        var providers = CommandHelpers.SplitList(settings.Providers)
            .Select(ProviderNames.Parse)
            .Distinct()
            .ToList();
        var description = settings.Description ?? $"Proposed {name} for the approved catalog.";

        Directory.CreateDirectory(assetRoot);
        if (input is { IsExternal: true })
        {
            var (url, reference) = PinExternal(input.Value, settings.Ref, session.Paths.WorkingDirectory);
            File.WriteAllText(Path.Combine(assetRoot, "agentpack.yaml"), SubmissionScaffolder.Manifest(
                kind, name, version.ToString(), description, groups, providers,
                externalSource: (url, reference, NormalizeOptional(settings.License)), hook, mcp));
            Output.Info($"Pinned external source at {reference}.");
        }
        else
        {
            var contentRoot = Path.Combine(assetRoot, "content");
            if (kind == AssetKind.Mcp)
            {
                Directory.CreateDirectory(contentRoot);
                File.WriteAllText(Path.Combine(contentRoot, "mcp.json"), "{}\n");
            }
            else
            {
                CopyInput(local!, contentRoot);
            }

            File.WriteAllText(Path.Combine(assetRoot, "agentpack.yaml"), SubmissionScaffolder.Manifest(
                kind, name, version.ToString(), description, groups, providers, externalSource: null, hook, mcp));
        }

        // From here the checkout holds committed work or a diagnosable failure, so it is
        // kept until the pull request actually exists.
        keepCheckout = true;
        var summary = update ? $"Update {id} to {version} in catalog" : $"Submit {id} to catalog";

        PrepareCatalog(session.Paths, submission, id);
        git.Run(["add", "--all", "--", $"assets/{kind.Display()}/{id}", "catalog.lock.yaml"], submission, "Git could not stage proposal");
        git.Run(["commit", "-m", summary], submission, "Git could not commit proposal");

        if (settings.PrepareOnly)
        {
            Output.Success($"Prepared '{id}' on branch {branch}.");
            Output.Info($"Proposal checkout: {submission}");
            Output.Info("Nothing was pushed and no pull request was opened (--prepare-only).");
            return ExitCodes.Ok;
        }

        var targetRepository = ExternalSourceParser.GitHubSlug(source.Url)
            ?? throw new AgentPackException(
                "Automatic pull request creation currently requires the active catalog to be hosted on GitHub.",
                $"The proposal is committed locally at {submission}; push it to your catalog host manually.");
        var (pushRemote, prHead) = PreparePushTarget(targetRepository, branch, submission);
        git.Run(["push", "--set-upstream", pushRemote, branch], submission, "Git could not push proposal branch");
        var action = update ? "Updates" : "Proposes";
        var prArgs = new List<string>
        {
            "pr", "create", "--title", summary,
            "--body", $"{action} `{id}` ({kind.Display()}) at version `{version}` through `agentpack submit`.\n\nSource: `{settings.Source}`",
            "--repo", targetRepository,
            "--base", source.Branch,
            "--head", prHead
        };
        if (settings.Draft) prArgs.Add("--draft");
        var pr = ProcessRunner.Run("gh", prArgs, submission);
        if (pr.ExitCode != 0)
        {
            throw new AgentPackException(
                $"The proposal branch was pushed, but GitHub could not open the pull request: {ProcessRunner.FirstLine(pr.Error)}",
                $"Run 'gh pr create --base {source.Branch} --head {branch}' in {submission}.");
        }

        // The PR now owns the branch; the local clone is disposable.
        keepCheckout = false;
        Output.Success($"{(update ? "Updated" : "Submitted")} '{id}' for catalog review.");
        Output.Info(pr.Output.Trim());
        Output.Info("The asset becomes installable after the pull request is approved and merged; no NuGet release is needed.");
        return ExitCodes.Ok;
    }

    private static (string Remote, string Head) PreparePushTarget(
        string targetRepository,
        string branch,
        string submission)
    {
        var permission = ProcessRunner.Run(
            "gh", ["repo", "view", targetRepository, "--json", "viewerPermission", "--jq", ".viewerPermission"], submission);
        var canPush = permission.ExitCode == 0 && permission.Output.Trim() is "ADMIN" or "MAINTAIN" or "WRITE";
        if (canPush)
        {
            Output.Info("GitHub access: proposal branch will be pushed to the catalog repository (never to main).");
            return ("origin", branch);
        }

        const string forkRemote = "agentpack-submit-fork";
        var fork = ProcessRunner.Run(
            "gh", ["repo", "fork", "--remote", "--remote-name", forkRemote], submission);
        if (fork.ExitCode != 0)
        {
            throw new AgentPackException(
                $"Could not create or connect your catalog fork: {ProcessRunner.FirstLine(fork.Error)}",
                $"The proposal is committed locally at {submission}; no branch was pushed.");
        }

        var user = ProcessRunner.Run("gh", ["api", "user", "--jq", ".login"], submission);
        var login = user.ExitCode == 0 ? user.Output.Trim() : "";
        if (string.IsNullOrWhiteSpace(login))
        {
            throw new AgentPackException(
                "GitHub could not determine the authenticated account for the proposal fork.",
                $"Run 'gh auth status'. The proposal remains committed locally at {submission}.");
        }

        Output.Info($"GitHub access: proposal branch will be pushed to {login}'s fork, then opened against {targetRepository}.");
        return (forkRemote, $"{login}:{branch}");
    }

    private static void PrepareCatalog(AgentPackPaths callerPaths, string submission, string submittedId)
    {
        var paths = new AgentPackPaths(callerPaths.Home, submission, callerPaths.ProviderHome);
        var sources = new SourceManager(paths);
        var loader = new CatalogLayerLoader(sources);
        var loaded = loader.Load(Path.Combine(submission, "catalog.yaml"));

        // Only the submitted asset is re-locked. Regenerating the whole file would clone
        // every external asset in the catalog before a one-asset PR could be opened, and
        // would put checksum churn for untouched assets in front of the reviewer.
        var submitted = loaded.Catalog.Assets.Single(x => x.Id.Equals(submittedId, StringComparison.OrdinalIgnoreCase));
        var onlySubmitted = loaded with { Catalog = loaded.Catalog with { Assets = [submitted] } };
        var generated = new CatalogLockWriter(paths).Generate(onlySubmitted, fetchExternal: true);
        foreach (var message in generated.Messages) Output.Warning(message);

        var merged = new CatalogLockFile { SchemaVersion = loaded.Lock.SchemaVersion };
        merged.Entries.AddRange(loaded.Lock.Entries
            .Where(x => !x.Id.Equals(submittedId, StringComparison.OrdinalIgnoreCase))
            .Concat(generated.Lock.Entries)
            .OrderBy(x => x.Id, StringComparer.Ordinal));
        merged.Save(CatalogLockFile.PathFor(loaded.PrimaryCatalogPath));

        // Only the lockfile changed since the load above, so re-reading every manifest
        // would tell us nothing new. Checksums are equally pointless to re-verify: the
        // submitted asset was just hashed, and hashing the rest of the catalog would put
        // the reviewer's whole content tree through SHA-256 on every proposal.
        var validated = loaded with { Lock = merged };
        ValidateResolvedSubmission(validated, submittedId, generated.ContentPaths);
        var report = new CatalogValidator().Validate(validated, verifyChecksums: false);
        Output.Report(report);
        if (!report.IsValid)
        {
            throw new AgentPackException(
                "The catalog proposal did not pass validation.",
                $"Fix the proposal in {submission}; nothing was pushed.",
                ExitCodes.ValidationFailed);
        }
    }

    /// <summary>
    /// The already-selected catalog, used only to tell "add" from "update" before paying
    /// for a clone. A load failure is not fatal: the clone re-checks authoritatively.
    /// </summary>
    private static Asset? FindExistingAsset(CliSession session, string id)
    {
        try
        {
            return session.LoadCatalog().Catalog.Assets
                .FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        catch (AgentPackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Add-vs-update is checked twice: once against the already-selected catalog so a
    /// wrong id fails in a second, and again against the fresh clone, which is the only
    /// authoritative answer. Both report it the same way.
    /// </summary>
    private static void EnsureMatchesCatalog(string id, bool exists, bool update, SemVersion? existingVersion)
    {
        if (update && !exists)
        {
            throw new AgentPackException(
                $"Asset '{id}' is not in the catalog, so there is nothing to update.",
                "Submit it without --update to add it.");
        }

        if (!update && exists)
        {
            var at = existingVersion is null ? "" : $" at version {existingVersion}";
            throw new AgentPackException(
                $"Asset '{id}' is already in the catalog{at}.",
                "Rerun with --update to propose a new revision, or pick a different --id.");
        }
    }

    private static SemVersion ResolveVersion(Settings settings, string id, Asset? existing, bool update)
    {
        EnsureMatchesCatalog(id, existing is not null, update, existing?.Version);

        if (settings.Version is { } requested)
        {
            if (!SemVersion.TryParse(requested, out var explicitVersion))
            {
                throw new AgentPackException(
                    $"Asset version '{requested}' is not valid semantic versioning.",
                    "Use MAJOR.MINOR.PATCH, for example --version 1.0.0.");
            }

            if (existing is not null && explicitVersion <= existing.Version)
            {
                throw new AgentPackException(
                    $"Version {explicitVersion} is not newer than the catalog's {existing.Version} for '{id}'.",
                    $"Installed copies only update on a higher version — use --version {Bump(existing.Version)} or higher.");
            }

            return explicitVersion;
        }

        // Default to something installable: a first release, or the next patch so existing
        // installs actually see the change on 'agentpack update'.
        return existing is null ? new SemVersion(1, 0, 0, null) : Bump(existing.Version);
    }

    private static SemVersion Bump(SemVersion version) =>
        new(version.Major, version.Minor, version.Patch + 1, null);

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A leftover checkout is untidy, never a reason to fail the command.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static SubmittedHook BuildHook(Settings settings, SubmissionInput input, LocalSubmission? local)
    {
        var timeout = settings.Timeout ?? 30;
        if (timeout <= 0)
        {
            throw new AgentPackException("Hook timeout must be greater than zero.", "Use --timeout 30 or another positive value.");
        }

        var trigger = EnumParsers.ParseTrigger(settings.Trigger, "submitted hook");
        var requested = NormalizeOptional(settings.Command);
        if (input.IsExternal && requested is null)
        {
            throw new AgentPackException(
                "An external hook needs its entry file specified with --command.",
                "For example: agentpack submit hook <url> --command scripts/check.sh");
        }

        var command = requested is not null
            ? NormalizeRelativeCommand(requested)
            : InferHookCommand(local!);
        if (local is not null && !local.Files.Any(x =>
                x.RelativePath.Equals(command, StringComparison.OrdinalIgnoreCase)))
        {
            throw new AgentPackException(
                $"Hook command '{command}' is not one of the submitted files.",
                "Pass a relative file path shown in the submission preview.");
        }

        return new SubmittedHook(trigger, NormalizeOptional(settings.Tool), command, timeout);
    }

    private static SubmittedMcp BuildMcp(Settings settings, string id)
    {
        var command = NormalizeOptional(settings.Command);
        var url = NormalizeOptional(settings.McpUrl);
        if ((command is null) == (url is null))
        {
            throw new AgentPackException(
                "An MCP submission needs exactly one connection: --command for stdio or --url for HTTP/SSE.",
                $"Examples: agentpack submit mcp {id} --command my-server, or agentpack submit mcp {id} --url https://example.com/mcp");
        }

        if (command is not null && command.Equals("replace-me", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentPackException("The MCP command must be a real executable, not a placeholder.");
        }

        var inferred = command is not null ? McpTransport.Stdio : McpTransport.Http;
        var transport = string.IsNullOrWhiteSpace(settings.Transport)
            ? inferred
            : EnumParsers.ParseTransport(settings.Transport, "submitted MCP server");
        if (command is not null && transport != McpTransport.Stdio || url is not null && transport == McpTransport.Stdio)
        {
            throw new AgentPackException(
                $"MCP transport '{transport.ToString().ToLowerInvariant()}' does not match the supplied connection.",
                "Use stdio with --command, or http/sse with --url.");
        }

        if (url is not null) ValidateMcpUrl(url);
        if (command is null && settings.Args.Length > 0)
        {
            throw new AgentPackException("--arg is only valid for a stdio MCP server that uses --command.");
        }

        var envVars = CommandHelpers.SplitList(settings.EnvVars)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var envVar in envVars) ValidateEnvName(envVar, "--env");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in settings.HeaderEnvVars)
        {
            var separator = value.IndexOf('=');
            if (separator <= 0 || separator == value.Length - 1)
            {
                throw new AgentPackException(
                    $"Invalid --header-env value '{value}'.",
                    "Use HEADER=ENV_NAME, for example Authorization=GITHUB_TOKEN. Never pass the secret value.");
            }

            var header = value[..separator].Trim();
            var envVar = value[(separator + 1)..].Trim();
            if (header.Any(char.IsControl) || header.Contains(':'))
            {
                throw new AgentPackException($"Invalid MCP header name '{header}'.");
            }

            ValidateEnvName(envVar, "--header-env");
            headers[header] = envVar;
        }

        if (command is not null && headers.Count > 0)
        {
            throw new AgentPackException("--header-env is only valid for an HTTP or SSE MCP server.");
        }

        return new SubmittedMcp(id, transport, command, settings.Args, envVars, url, headers);
    }

    private static void ValidateKindOptions(AssetKind kind, Settings settings)
    {
        if (kind != AssetKind.Mcp &&
            (settings.Args.Length > 0 || settings.EnvVars.Length > 0 || settings.HeaderEnvVars.Length > 0 ||
             settings.McpUrl is not null || settings.Transport is not null))
        {
            throw new AgentPackException("MCP options (--url, --transport, --arg, --env, --header-env) require kind 'mcp'.");
        }

        if (kind != AssetKind.Hooks && kind != AssetKind.Mcp && settings.Command is not null)
        {
            throw new AgentPackException("--command is only valid for hooks and MCP servers.");
        }

        if (kind != AssetKind.Hooks && (settings.Trigger is not null || settings.Tool is not null || settings.Timeout is not null))
        {
            throw new AgentPackException("Hook options (--trigger, --tool, --timeout) require kind 'hook'.");
        }

        if (kind == AssetKind.Mcp && (settings.Ref is not null || settings.License is not null))
        {
            throw new AgentPackException("MCP submissions are typed configurations; --ref and --license apply only to external content URLs.");
        }
    }

    private static void ShowSubmissionPreview(
        AgentPackSource catalog,
        SubmissionPlan plan,
        string? requestedRef,
        bool update)
    {
        var (kind, id, _, version, input, local, hook, mcp) = plan;
        Output.Info($"Catalog: {catalog.Name} ({catalog.Url})");
        Output.Info($"Proposal: {(update ? "update" : "add")} {id} ({kind.Display()}) {version}");
        if (input is { IsExternal: true })
        {
            Output.Info($"External source: {input.Value}");
            var (_, suppliedRef) = ExternalSourceParser.SplitShorthand(input.Value);
            var selectedRef = NormalizeOptional(requestedRef) ?? suppliedRef;
            Output.Info(selectedRef is null
                ? "Source revision: latest current commit (resolved and pinned once)"
                : $"Source revision: {selectedRef} (immutable pin)");
        }

        if (local is not null)
        {
            Output.Info($"Local source: {input!.Value}");
            Output.Info($"Files to include: {local.Files.Count} ({ByteSize.Format(local.TotalBytes)})");
            foreach (var file in local.Files.Take(20)) Output.Info($"  + {file.RelativePath}");
            if (local.Files.Count > 20) Output.Info($"  + ... and {local.Files.Count - 20} more");
            foreach (var ignored in local.Ignored.Take(10)) Output.Info($"  - ignored: {ignored}");
            if (local.Ignored.Count > 10) Output.Info($"  - ... and {local.Ignored.Count - 10} more ignored paths");
        }

        if (hook is not null)
        {
            Output.Info($"Hook: {EnumParsers.CamelCase(hook.Trigger.ToString())} -> {hook.Command} ({hook.TimeoutSec}s)");
        }

        if (mcp is not null)
        {
            Output.Info(mcp.Transport == McpTransport.Stdio
                ? $"MCP: stdio -> {mcp.Command}"
                : $"MCP: {mcp.Transport.ToString().ToLowerInvariant()} -> {mcp.Url}");
            if (mcp.EnvVars.Count > 0) Output.Info($"Required environment names: {string.Join(", ", mcp.EnvVars)}");
            if (mcp.HeaderEnvVars.Count > 0) Output.Info($"Environment-backed headers: {string.Join(", ", mcp.HeaderEnvVars.Keys)}");
        }
    }

    private static void ValidateResolvedSubmission(
        LoadedCatalog loaded,
        string id,
        IReadOnlyDictionary<string, string> contentPaths)
    {
        var asset = loaded.Catalog.Assets.Single(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (asset.Kind == AssetKind.Mcp) return;

        // Generating the lock just fetched and hashed this content. Resolving it again
        // would re-verify the cache entry we already produced, hashing the whole tree twice.
        if (!contentPaths.TryGetValue(id, out var content))
        {
            // Lock generation only skips an asset whose local content is missing, which the
            // validator reports immediately after this. External content is always resolved,
            // so a missing entry there must never quietly bypass the scan below.
            if (asset.Source is AssetSource.External)
            {
                throw new AgentPackException(
                    $"External content for '{id}' was not resolved, so it could not be checked.",
                    "Retry the submission; nothing was pushed.");
            }

            return;
        }

        // Local content was copied from an already-scanned whitelist, so re-scanning it
        // can only re-confirm the first scan. External content is seen here for the
        // first time and must go through the same safety rules.
        if (asset.Source is AssetSource.External) _ = LocalSubmissionScanner.Scan(content, asset.Kind);

        // CatalogValidator applies the same rule to local content it can see on disk;
        // this covers the resolved external case, which it deliberately does not fetch.
        if (asset.Source is AssetSource.External &&
            asset.Hook is { Command: { } command } &&
            HookCommand.ResolveInside(content, command) is null)
        {
            throw new AgentPackException(
                $"Hook command '{command}' was not found in the resolved content for '{id}'.",
                "Correct --command and retry; nothing was pushed.");
        }
    }

    private static string InferHookCommand(LocalSubmission local)
    {
        if (local.Files.Count == 1) return local.Files[0].RelativePath;
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".sh", ".bash", ".zsh", ".ps1", ".py", ".js", ".mjs", ".cjs", ".ts", ".rb"
        };
        var candidates = local.Files.Where(x => extensions.Contains(Path.GetExtension(x.RelativePath))).ToList();
        if (candidates.Count == 1) return candidates[0].RelativePath;
        throw new AgentPackException(
            "The hook folder has more than one possible entry file.",
            "Pass --command <relative-file>, for example --command scripts/check-secrets.sh.");
    }

    private static string NormalizeRelativeCommand(string value)
    {
        if (HookCommand.Normalize(value) is not { } normalized)
        {
            throw new AgentPackException(
                $"Hook command must be a safe relative file path, not '{value}'.",
                "Use a path inside the submitted hook folder, such as scripts/check.sh.");
        }

        return normalized;
    }

    // Both rules belong to the catalog, not to submission: rejecting them here only
    // turns what would be a validation failure after the clone into an instant error.
    private static void ValidateMcpUrl(string value)
    {
        if (!CatalogValidator.IsSafeMcpUrl(value))
        {
            throw new AgentPackException(
                $"MCP URL '{value}' must be an absolute HTTPS URL.",
                "Plain HTTP is accepted only for localhost development servers.");
        }
    }

    private static void ValidateEnvName(string value, string option)
    {
        if (!CatalogValidator.IsValidEnvName(value))
        {
            throw new AgentPackException(
                $"{option} expects an environment variable name, not '{value}'.",
                "Use a name such as GITHUB_TOKEN. Never put a secret value in the catalog.");
        }
    }

    private static SubmissionInput ResolveInput(string value, string workingDirectory)
    {
        if (LooksExternal(value)) return new SubmissionInput(value.Trim(), true);
        var path = Path.GetFullPath(value, workingDirectory);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new AgentPackException($"Submission source was not found: {path}");
        }

        return new SubmissionInput(path, false);
    }

    private static bool LooksExternal(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ssh" ||
        value.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
        value.EndsWith(".git", StringComparison.OrdinalIgnoreCase);

    private static string SuggestedId(SubmissionInput input)
    {
        if (!input.IsExternal)
        {
            if (File.Exists(input.Value))
            {
                var fileName = Path.GetFileName(input.Value);
                return fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(Path.GetDirectoryName(input.Value)) ?? "skill"
                    : Path.GetFileNameWithoutExtension(input.Value);
            }

            return Path.GetFileName(input.Value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        var (url, _) = ExternalSourceParser.SplitShorthand(input.Value);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var candidate = uri.Segments.LastOrDefault()?.Trim('/') ?? "asset";
            if (candidate.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase) && uri.Segments.Length > 1)
            {
                candidate = uri.Segments[^2].Trim('/');
            }
            return candidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? candidate[..^4] : candidate;
        }

        return Path.GetFileNameWithoutExtension(url);
    }

    private static string NormalizeId(string value)
    {
        var id = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new AgentPackException("Could not derive a valid asset id.", "Pass a kebab-case id with --id <id>.");
        }

        return id;
    }

    private static (string Url, string Ref) PinExternal(string value, string? requestedRef, string workingDirectory)
    {
        var (url, suppliedRef) = ExternalSourceParser.SplitShorthand(value);
        var selectedRef = NormalizeOptional(requestedRef) ?? suppliedRef;
        if (!string.IsNullOrWhiteSpace(selectedRef))
        {
            if (!CatalogValidator.IsPinnedExternalRef(selectedRef))
            {
                throw new AgentPackException(
                    $"'{selectedRef}' is not an immutable external ref.",
                    "Use a full commit SHA or immutable tag, or omit --ref to pin the latest current commit.");
            }

            return (url, selectedRef);
        }

        var (repo, branch) = ExternalSourceParser.RepositoryAndBranch(url);
        var gitRef = branch is null ? "HEAD" : $"refs/heads/{branch}";
        var result = ProcessRunner.Run("git", ["ls-remote", "--", repo, gitRef], workingDirectory);
        if (result.ExitCode != 0)
        {
            throw new AgentPackException(
                $"Could not resolve the external source to a commit: {ProcessRunner.FirstLine(result.Error)}",
                "Check the URL and your git credentials, or append @<full-commit-sha>.");
        }

        var reference = result.Output.Split(['\t', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (reference is null || !CatalogValidator.IsPinnedExternalRef(reference))
        {
            throw new AgentPackException("The external source did not resolve to a full commit SHA.");
        }

        return (url, reference);
    }

    private static void CopyInput(LocalSubmission submission, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in submission.Files)
        {
            var target = Path.Combine(destination, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            ContentHash.CopyTree(file.FullPath, target);
        }
    }

    private static void EnsureGitHubCli(string workingDirectory)
    {
        try
        {
            // 'auth status' answers both questions at once: a missing binary fails to
            // start, an unauthenticated one exits non-zero. No separate version probe.
            if (ProcessRunner.Run("gh", ["auth", "status"], workingDirectory).ExitCode == 0) return;
        }
        catch
        {
            // Render the same actionable product error for missing and unauthenticated gh.
        }

        throw new AgentPackException(
            "Automatic submission requires an authenticated GitHub CLI.",
            "Install 'gh', run 'gh auth login', and retry; or use --prepare-only to create a local proposal branch.");
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record SubmissionInput(string Value, bool IsExternal);

    /// <summary>
    /// Everything Execute resolves about the proposal before any clone happens. Kept
    /// together so the preview and the publish step cannot disagree about what is
    /// being submitted.
    /// </summary>
    private sealed record SubmissionPlan(
        AssetKind Kind,
        string Id,
        string Name,
        SemVersion Version,
        SubmissionInput? Input,
        LocalSubmission? Local,
        SubmittedHook? Hook,
        SubmittedMcp? Mcp);
}
