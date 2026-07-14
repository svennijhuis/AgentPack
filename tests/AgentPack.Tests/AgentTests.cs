using System.Text.RegularExpressions;
using AgentPack.Core;
using AgentPack.Core.Primitives;

namespace AgentPack.Tests;

public class AgentTests
{
    [Fact]
    public void ExternalFrontmatterIsOnlyAnAuthoringSuggestionAndMapsKnownTools()
    {
        using var temp = new TempDir();
        var source = Path.Combine(temp.Path, "governance.agent.md");
        File.WriteAllText(source, """
            ---
            name: Agent Governance Reviewer
            description: Reviews governance controls.
            model: gpt-4o
            tools: [codebase, terminalCommand, companyCustomTool]
            ---

            Review governance.
            """);

        var inspection = ExternalAgentFrontmatter.Inspect(source);

        Assert.NotNull(inspection);
        Assert.Equal("Agent Governance Reviewer", inspection.Name);
        Assert.Equal("Reviews governance controls.", inspection.Description);
        Assert.Equal("gpt-4o", inspection.Model);
        Assert.Equal([AgentTool.Read, AgentTool.Search, AgentTool.Execute], inspection.SuggestedTools);
        Assert.Equal(["companyCustomTool"], inspection.UnknownTools);
    }

    [Fact]
    public void CodexOutputUsesARealTomlParser()
    {
        var agent = TestData.Asset(AssetKind.Agents, "bad", agent: new AgentSpec());
        var ex = Assert.Throws<AgentPackException>(() =>
            CatalogCompiler.ValidateSyntax("name = \"unterminated", agent, ProviderName.Codex));
        Assert.Contains("agent.compile.syntax", ex.Message);
    }

    [Fact]
    public void RendererRejectsMissingDescriptionBeforeWriting()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var agent = Agent(paths, "undiscoverable") with { Description = "" };
        var loaded = TestData.Loaded(paths.WorkingDirectory, agent);

        var ex = Assert.Throws<AgentPackException>(() =>
            new AgentRenderer(paths).Render(loaded, agent, ProviderName.Claude, InstallScope.Project));

        Assert.Contains("[agent.description.missing]", ex.Message);
        Assert.Equal(ExitCodes.ValidationFailed, ex.ExitCode);
    }

    [Fact]
    public void DocumentedNativeExamplesParseWithProductionCompilers()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "docs", "agent-authoring.md")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var markdown = File.ReadAllText(Path.Combine(directory!.FullName, "docs", "agent-authoring.md"));
        var agent = TestData.Asset(AssetKind.Agents, "agent-governance-reviewer", agent: new AgentSpec());
        foreach (var provider in ProviderNames.All)
        {
            var language = provider == ProviderName.Codex ? "toml" : "markdown";
            var pattern = $"<!-- agentpack-compile-example:{provider.Display()} -->\\s*```{language}\\n(?<content>.*?)\\n```";
            var match = Regex.Match(markdown, pattern, RegexOptions.Singleline);
            Assert.True(match.Success, $"Missing documented {provider.Display()} compile example.");
            CatalogCompiler.ValidateSyntax(match.Groups["content"].Value + "\n", agent, provider);
        }
    }

    [Fact]
    public void AgentPlanOrdersAndDeduplicatesAutomaticDependencies()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var skill = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "analysis");
        var first = Agent(paths, "first", skills: ["analysis"]);
        var second = Agent(paths, "second", skills: ["analysis"]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, skill, first, second);

        var plan = new Installer(paths).Plan(loaded, [first, second], [ProviderName.Claude], InstallScope.Project);

        Assert.Equal(["analysis", "first", "second"], plan.Items.Select(x => x.Asset.Id));
        var dependency = plan.Items[0];
        Assert.False(dependency.Direct);
        Assert.Equal(["agent:first", "agent:second"], dependency.RequiredBy);
    }

    [Fact]
    public void InstallingSecondAgentAddsOwnershipToExistingDependency()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var skill = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "analysis");
        var first = Agent(paths, "first", skills: ["analysis"]);
        var second = Agent(paths, "second", skills: ["analysis"]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, skill, first, second);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [first], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Overwrite);
        installer.Apply(installer.Plan(loaded, [second], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        var dependency = JsonStore.Load<AgentPackLock>(paths.ProjectLockPath).Find("analysis", ProviderName.Claude, AssetKind.Skills)!;
        Assert.Equal(["agent:first", "agent:second"], dependency.RequiredBy);
    }

    [Fact]
    public void NativeAgentsEmbedInstructionsStripExternalFrontmatterAndInstallSkills()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var instruction = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Instructions, "rules",
            files: new Dictionary<string, string> { ["rules.md"] = "---\napplyTo: '**'\n---\nNever skip tests.\n" });
        var skill = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "analysis");
        var agent = Agent(paths, "upgrade", instructions: ["rules"], skills: ["analysis"]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, instruction, skill, agent);
        var installer = new Installer(paths);

        var plan = installer.Plan(loaded, [agent], ProviderNames.All, InstallScope.Project);
        installer.Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".claude", "agents", "upgrade.md")));
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".codex", "agents", "upgrade.toml")));
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".github", "agents", "upgrade.agent.md")));
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".cursor", "agents", "upgrade.md")));
        var claude = File.ReadAllText(Path.Combine(paths.WorkingDirectory, ".claude", "agents", "upgrade.md"));
        Assert.Contains("Never skip tests.", claude);
        Assert.DoesNotContain("applyTo:", claude);
        Assert.Contains("skills: [\"analysis\"]", claude);
        Assert.True(Directory.Exists(Path.Combine(paths.WorkingDirectory, ".claude", "skills", "analysis")));
        Assert.False(File.Exists(Path.Combine(paths.WorkingDirectory, "AGENTS.md")));
    }

    [Fact]
    public void UpstreamModelMetadataIsStrippedFromEveryNativeFormat()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var agent = TestData.WriteLocalAsset(
            paths.WorkingDirectory,
            AssetKind.Agents,
            "governance",
            files: new Dictionary<string, string>
            {
                ["AGENT.md"] = "---\nname: upstream\nmodel: gpt-4o\ntools: [codebase]\n---\nReview governance.\n"
            },
            agent: new AgentSpec { Tools = [AgentTool.Read, AgentTool.Search] });
        var loaded = TestData.Loaded(paths.WorkingDirectory, agent);
        var renderer = new AgentRenderer(paths);

        var claude = renderer.Render(loaded, agent, ProviderName.Claude, InstallScope.Project);
        var codex = renderer.Render(loaded, agent, ProviderName.Codex, InstallScope.Project);
        var copilot = renderer.Render(loaded, agent, ProviderName.Copilot, InstallScope.Project);
        var cursor = renderer.Render(loaded, agent, ProviderName.Cursor, InstallScope.Project);

        Assert.Contains("description: \"Test asset.\"", claude);
        Assert.DoesNotContain("model:", claude, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model =", codex, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model:", copilot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("model:", cursor, StringComparison.OrdinalIgnoreCase);
        CatalogCompiler.ValidateSyntax(claude, agent, ProviderName.Claude);
        CatalogCompiler.ValidateSyntax(codex, agent, ProviderName.Codex);
        CatalogCompiler.ValidateSyntax(copilot, agent, ProviderName.Copilot);
        CatalogCompiler.ValidateSyntax(cursor, agent, ProviderName.Cursor);

        var compile = new CatalogCompiler(paths).Compile(loaded);
        var warning = Assert.Single(compile.Warnings);
        Assert.Equal("agent.model.stripped", warning.Code);
        Assert.Contains("gpt-4o", warning.Message);
        Assert.Contains("current/default", warning.Message);
    }

    [Fact]
    public void PortableToolsMapExactlyOrUseDocumentedCoarseProjection()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var full = Agent(paths, "full", tools:
            [AgentTool.Read, AgentTool.Search, AgentTool.Edit, AgentTool.Execute, AgentTool.Web, AgentTool.Agent]);
        var readOnly = Agent(paths, "read-only", tools: [AgentTool.Read, AgentTool.Search, AgentTool.Web]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, full, readOnly);
        var renderer = new AgentRenderer(paths);

        var claude = renderer.Render(loaded, full, ProviderName.Claude, InstallScope.Project);
        var copilot = renderer.Render(loaded, full, ProviderName.Copilot, InstallScope.Project);
        var codex = renderer.Render(loaded, readOnly, ProviderName.Codex, InstallScope.Project);
        var cursor = renderer.Render(loaded, readOnly, ProviderName.Cursor, InstallScope.Project);

        Assert.Contains("tools: [\"Read\", \"Glob\", \"Grep\", \"Edit\", \"Write\", \"Bash\", \"WebFetch\", \"WebSearch\", \"Agent\"]", claude);
        Assert.Contains("tools: [\"read\", \"search\", \"edit\", \"execute\", \"web\", \"agent\"]", copilot);
        Assert.Contains("sandbox_mode = \"read-only\"", codex);
        Assert.Contains("readonly: true", cursor);
        Assert.DoesNotContain("tools:", codex);
        Assert.DoesNotContain("tools:", cursor);
    }

    [Fact]
    public void CursorInstallsImportedMcpGloballyWhileOtherProvidersKeepItAgentLocal()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var mcp = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "docs", mcp: new McpServer
        {
            Server = "docs",
            Transport = McpTransport.Http,
            Url = "https://example.test/mcp",
            Tools = ["search"],
            HeaderEnvVars = new Dictionary<string, string> { ["Authorization"] = "DOCS_TOKEN" }
        });
        var agent = Agent(paths, "upgrade", mcp: ["docs"]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, mcp, agent);

        var plan = new Installer(paths).Plan(loaded, [agent], ProviderNames.All, InstallScope.Project);

        Assert.Single(plan.Items, x => x.Asset.Id == "docs");
        Assert.Equal(ProviderName.Cursor, plan.Items.Single(x => x.Asset.Id == "docs").Provider);
        new Installer(paths).Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Overwrite);
        Assert.True(File.Exists(Path.Combine(paths.WorkingDirectory, ".cursor", "mcp.json")));
        var claude = File.ReadAllText(Path.Combine(paths.WorkingDirectory, ".claude", "agents", "upgrade.md"));
        var codex = File.ReadAllText(Path.Combine(paths.WorkingDirectory, ".codex", "agents", "upgrade.toml"));
        var copilot = File.ReadAllText(Path.Combine(paths.WorkingDirectory, ".github", "agents", "upgrade.agent.md"));
        var cursor = File.ReadAllText(Path.Combine(paths.WorkingDirectory, ".cursor", "mcp.json"));
        Assert.Contains("mcpServers: [{", claude);
        Assert.Contains("${DOCS_TOKEN}", claude);
        Assert.Contains("[mcp_servers.docs]", codex);
        Assert.Contains("Authorization = \"DOCS_TOKEN\"", codex);
        Assert.Contains("${DOCS_TOKEN}", copilot);
        Assert.Contains("${env:DOCS_TOKEN}", cursor);
    }

    [Fact]
    public void VersionConflictHasStableTypedError()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var skill = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "analysis", version: "2.1.0");
        var agent = Agent(paths, "upgrade", skillRefs:
            [new AgentAssetReference("analysis", SemVersionRange.Parse(">=1.2.0 <2.0.0"))]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, skill, agent);

        var ex = Assert.Throws<AgentPackException>(() =>
            new Installer(paths).Plan(loaded, [agent], [ProviderName.Claude], InstallScope.Project));
        Assert.Contains("[agent.dependency.version]", ex.Message);
        Assert.Equal(ExitCodes.ValidationFailed, ex.ExitCode);
    }

    [Fact]
    public void RemovingAgentLeavesSharedDependencyAsPrunableOrphan()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var skill = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "analysis");
        var agent = Agent(paths, "upgrade", skills: ["analysis"]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, skill, agent);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [agent], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        installer.Remove(AssetKind.Agents, ["upgrade"], null, InstallScope.Project);
        var orphan = Assert.Single(installer.Prune(InstallScope.Project, null, apply: false).Clean);
        Assert.Equal("analysis", orphan.Id);
        installer.Prune(InstallScope.Project, null, apply: true);
        Assert.Empty(JsonStore.Load<AgentPackLock>(paths.ProjectLockPath).Entries);
    }

    [Fact]
    public void KeepingModifiedAgentSkipsItsPendingDependencyUpdate()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var skill = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "analysis");
        var agent = Agent(paths, "upgrade", skills: ["analysis"]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, skill, agent);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [agent], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        File.WriteAllText(Path.Combine(paths.WorkingDirectory, ".claude", "agents", "upgrade.md"), "local agent\n");
        File.WriteAllText(Path.Combine(paths.WorkingDirectory, "assets", "skills", "analysis", "content", "SKILL.md"), "# changed\n");
        var newerSkill = skill with { Version = SemVersion.Parse("1.1.0") };
        var changed = TestData.Loaded(paths.WorkingDirectory, newerSkill, agent);
        var plan = installer.Plan(changed, [agent], [ProviderName.Claude], InstallScope.Project);
        var results = installer.Apply(plan.Items, changed, InstallScope.Project, _ => DriftAction.Keep);

        Assert.Contains(results, x => x.Item.Asset.Id == "analysis" && x.Outcome == ApplyOutcome.SkippedTransaction);
        Assert.Equal("1.0.0", JsonStore.Load<AgentPackLock>(paths.ProjectLockPath).Find("analysis", ProviderName.Claude, AssetKind.Skills)!.Version);
    }

    [Fact]
    public void SharedDependencyUpdateRebuildsAllOwnersOrSkipsConnectedClosure()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var skill = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "analysis");
        var first = Agent(paths, "first", skills: ["analysis"]);
        var second = Agent(paths, "second", skills: ["analysis"]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, skill, first, second);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [first, second], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        var newerSkill = skill with { Version = SemVersion.Parse("1.1.0") };
        File.WriteAllText(Path.Combine(paths.WorkingDirectory, ".claude", "agents", "second.md"), "local second\n");
        var changed = TestData.Loaded(paths.WorkingDirectory, newerSkill, first, second);
        var plan = installer.Plan(changed, [first], [ProviderName.Claude], InstallScope.Project);
        Assert.Contains(plan.Items, x => x.Asset.Id == "first");
        Assert.Contains(plan.Items, x => x.Asset.Id == "second");

        var results = installer.Apply(plan.Items, changed, InstallScope.Project, _ => DriftAction.Keep);
        Assert.All(results, x => Assert.Contains(x.Outcome, new[] { ApplyOutcome.KeptLocalChanges, ApplyOutcome.SkippedTransaction }));
        Assert.Equal("1.0.0", JsonStore.Load<AgentPackLock>(paths.ProjectLockPath).Find("analysis", ProviderName.Claude, AssetKind.Skills)!.Version);
    }

    [Fact]
    public void PinnedDependencyUpdateBlocksAgentBeforeWriting()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var skill = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "analysis");
        var agent = Agent(paths, "upgrade", skills: ["analysis"]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, skill, agent);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [skill], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Overwrite);
        installer.SetPinned("analysis", true, InstallScope.Project);
        var newerSkill = skill with { Version = SemVersion.Parse("1.1.0") };
        var changed = TestData.Loaded(paths.WorkingDirectory, newerSkill, agent);

        var ex = Assert.Throws<AgentPackException>(() => installer.Apply(
            installer.Plan(changed, [agent], [ProviderName.Claude], InstallScope.Project).Items,
            changed, InstallScope.Project, _ => DriftAction.Overwrite));
        Assert.Contains("[agent.dependency.pinned]", ex.Message);
        Assert.False(File.Exists(Path.Combine(paths.WorkingDirectory, ".claude", "agents", "upgrade.md")));
    }

    [Fact]
    public void DependencyContentChangeMakesAgentOutdatedWithoutVersionBump()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var instruction = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Instructions, "rules",
            files: new Dictionary<string, string> { ["rules.md"] = "Version one.\n" });
        var agent = Agent(paths, "upgrade", instructions: ["rules"]);
        var loaded = TestData.Loaded(paths.WorkingDirectory, instruction, agent);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [agent], [ProviderName.Copilot], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        File.WriteAllText(Path.Combine(paths.WorkingDirectory, "assets", "instructions", "rules", "content", "rules.md"), "Version two.\n");
        var changed = TestData.Loaded(paths.WorkingDirectory, instruction, agent);
        var outdated = installer.Outdated(changed, InstallScope.Project);
        Assert.Contains(outdated.Items, x => x.Asset.Id == "upgrade" && x.State == InstallState.UpdateAvailable);
    }

    [Fact]
    public void IdenticalUnmanagedTargetIsAdoptedWithoutRewrite()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var skill = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "analysis");
        var loaded = TestData.Loaded(paths.WorkingDirectory, skill);
        var target = Path.Combine(paths.WorkingDirectory, ".claude", "skills", "analysis");
        ContentHash.CopyTree(Path.Combine(paths.WorkingDirectory, "assets", "skills", "analysis", "content"), target);
        var before = File.GetLastWriteTimeUtc(Path.Combine(target, "SKILL.md"));

        var installer = new Installer(paths);
        var plan = installer.Plan(loaded, [skill], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.Adoptable, Assert.Single(plan.Items).State);
        installer.Apply(plan.Items, loaded, InstallScope.Project, _ => throw new Exception("must not prompt"));

        Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(target, "SKILL.md")));
        Assert.Single(JsonStore.Load<AgentPackLock>(paths.ProjectLockPath).Entries);
    }

    private static Asset Agent(
        AgentPackPaths paths,
        string id,
        IReadOnlyList<string>? instructions = null,
        IReadOnlyList<string>? skills = null,
        IReadOnlyList<string>? mcp = null,
        IReadOnlyList<AgentAssetReference>? skillRefs = null,
        IReadOnlyList<AgentTool>? tools = null)
    {
        var spec = new AgentSpec
        {
            Tools = tools ?? [AgentTool.Read, AgentTool.Search, AgentTool.Edit, AgentTool.Execute],
            Imports = new AgentImports
            {
                Instructions = (instructions ?? []).Select(x => new AgentAssetReference(x, null)).ToList(),
                Skills = skillRefs ?? (skills ?? []).Select(x => new AgentAssetReference(x, null)).ToList(),
                Mcp = (mcp ?? []).Select(x => new AgentAssetReference(x, null)).ToList()
            }
        };
        return TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Agents, id, agent: spec);
    }
}
