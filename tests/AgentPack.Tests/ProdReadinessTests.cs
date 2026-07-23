using System.Text.Json.Nodes;
using AgentPack.Core;
using AgentPack.Core.Primitives;

namespace AgentPack.Tests;

/// <summary>
/// Regression tests for the production-readiness review fixes: fragment-based
/// drift tracking in shared configs, un-merging on remove, unmanaged-file
/// protection, git argument safety, provider-home resolution, config parse
/// errors, and backup pruning.
/// </summary>
public class SharedConfigDriftTests
{
    [Fact]
    public void TwoHooksInOneSharedConfigStayClean()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var first = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard-one", hook: new HookSpec { Command = "hook.sh" });
        var second = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard-two", hook: new HookSpec { Command = "hook.sh" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, first, second);
        var installer = new Installer(paths);

        installer.Apply(installer.Plan(loaded, [first, second], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        // Before fragments, the second merge into settings.json made the first
        // entry's whole-file checksum stale — false "local changes" forever.
        var replan = installer.Plan(loaded, [first, second], [ProviderName.Claude], InstallScope.Project);
        Assert.All(replan.Items, item => Assert.Equal(InstallState.Installed, item.State));
    }

    [Fact]
    public void TwoMcpServersInOneSharedConfigStayClean()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var github = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "github",
            mcp: new McpServer { Server = "github", Command = "github-mcp" });
        var jira = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "jira",
            mcp: new McpServer { Server = "jira", Command = "jira-mcp" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, github, jira);
        var installer = new Installer(paths);

        installer.Apply(installer.Plan(loaded, [github, jira], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        var replan = installer.Plan(loaded, [github, jira], [ProviderName.Claude], InstallScope.Project);
        Assert.All(replan.Items, item => Assert.Equal(InstallState.Installed, item.State));
    }

    [Fact]
    public void GenuineEditToOurFragmentIsStillDetected()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var hook = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard", hook: new HookSpec { Command = "hook.sh" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, hook);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [hook], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        var settingsPath = Path.Combine(paths.WorkingDirectory, ".claude", "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["timeout"] = 99;
        File.WriteAllText(settingsPath, settings.ToJsonString());

        var plan = installer.Plan(loaded, [hook], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.LocalChanges, Assert.Single(plan.Items).State);
    }

    [Fact]
    public void DriftOverwriteRestoresOurFragmentAndOnlyOurs()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var hook = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard", hook: new HookSpec { Command = "hook.sh", TimeoutSec = 30 });
        var loaded = TestData.Loaded(paths.WorkingDirectory, hook);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [hook], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        var settingsPath = Path.Combine(paths.WorkingDirectory, ".claude", "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["model"] = "opus";
        settings["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["timeout"] = 99;
        File.WriteAllText(settingsPath, settings.ToJsonString());

        var plan = installer.Plan(loaded, [hook], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.LocalChanges, Assert.Single(plan.Items).State);
        installer.Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        var restored = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        var handlers = restored["hooks"]!["PreToolUse"]![0]!["hooks"]!.AsArray();
        Assert.Single(handlers);
        Assert.Equal(30, handlers[0]!["timeout"]!.GetValue<int>());
        Assert.Equal("opus", restored["model"]!.GetValue<string>());
    }

    [Fact]
    public void UserEditsElsewhereInSharedConfigAreNotDrift()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var hook = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard", hook: new HookSpec { Command = "hook.sh" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, hook);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [hook], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        // The user adds their own setting and their own hook to the same file.
        var settingsPath = Path.Combine(paths.WorkingDirectory, ".claude", "settings.json");
        var settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
        settings["model"] = "opus";
        ((JsonArray)settings["hooks"]!["PreToolUse"]!).Add(new JsonObject
        {
            ["matcher"] = "Edit",
            ["hooks"] = new JsonArray { new JsonObject { ["type"] = "command", ["command"] = "./mine.sh" } }
        });
        File.WriteAllText(settingsPath, settings.ToJsonString());

        var plan = installer.Plan(loaded, [hook], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.Installed, Assert.Single(plan.Items).State);
    }

    [Fact]
    public void McpUpgradeWithChangedConfigReplacesInsteadOfConflicting()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var v1 = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "github",
            mcp: new McpServer { Server = "github", Command = "github-mcp" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, v1);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [v1], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        var v2 = v1 with
        {
            Version = SemVersion.Parse("1.1.0"),
            Mcp = new McpServer { Server = "github", Command = "github-mcp", EnvVars = ["GITHUB_TOKEN"] }
        };
        var loadedNewer = TestData.Loaded(paths.WorkingDirectory, v2);
        var plan = installer.Plan(loadedNewer, [v2], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.UpdateAvailable, Assert.Single(plan.Items).State);

        var results = installer.Apply(plan.Items, loadedNewer, InstallScope.Project, _ => DriftAction.Keep);
        Assert.Equal(ApplyOutcome.Updated, Assert.Single(results).Outcome);

        var mcpJson = JsonNode.Parse(File.ReadAllText(Path.Combine(paths.WorkingDirectory, ".mcp.json")))!.AsObject();
        var servers = mcpJson["mcpServers"]!.AsObject();
        Assert.Single(servers);
        Assert.Contains("GITHUB_TOKEN", servers["github"]!.ToJsonString());
    }

    [Fact]
    public void HookUpgradeReplacesTheHandlerInsteadOfStackingDuplicates()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var v1 = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard",
            hook: new HookSpec { Command = "hook.sh", TimeoutSec = 30 });
        var loaded = TestData.Loaded(paths.WorkingDirectory, v1);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [v1], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        var v2 = v1 with { Version = SemVersion.Parse("1.1.0"), Hook = new HookSpec { Command = "hook.sh", TimeoutSec = 60 } };
        var loadedNewer = TestData.Loaded(paths.WorkingDirectory, v2);
        installer.Apply(installer.Plan(loadedNewer, [v2], [ProviderName.Claude], InstallScope.Project).Items,
            loadedNewer, InstallScope.Project, _ => DriftAction.Keep);

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(paths.WorkingDirectory, ".claude", "settings.json")))!.AsObject();
        var handlers = settings["hooks"]!["PreToolUse"]![0]!["hooks"]!.AsArray();
        Assert.Single(handlers);
        Assert.Equal(60, handlers[0]!["timeout"]!.GetValue<int>());
    }

    [Fact]
    public void ForeignServerWithSameIdIsStillAConflict()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "github",
            mcp: new McpServer { Server = "github", Command = "github-mcp" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        File.WriteAllText(Path.Combine(paths.WorkingDirectory, ".mcp.json"),
            """{ "mcpServers": { "github": { "type": "stdio", "command": "the-users-own-server" } } }""");

        var installer = new Installer(paths);
        var ex = Assert.Throws<AgentPackException>(() =>
            installer.Apply(installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project).Items,
                loaded, InstallScope.Project, _ => DriftAction.Keep));
        Assert.Equal(ExitCodes.DriftOrConflict, ex.ExitCode);
    }

    [Fact]
    public void LegacyLockEntryWithoutFragmentDoesNotReportDrift()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var hook = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard", hook: new HookSpec { Command = "hook.sh" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, hook);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [hook], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        // Simulate a lockfile written before fragments existed.
        var lockFile = JsonStore.Load<AgentPackLock>(paths.ProjectLockPath);
        foreach (var entry in lockFile.Entries) entry.Fragment = null;
        JsonStore.Save(paths.ProjectLockPath, lockFile);

        var plan = installer.Plan(loaded, [hook], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.Installed, Assert.Single(plan.Items).State);
    }
}

public class RemoveUnmergesFragmentsTests
{
    [Fact]
    public void RemovingAHookDeletesItsRegistrationFromSharedConfig()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var mine = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard", hook: new HookSpec { Command = "hook.sh" });
        var other = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "keeper", hook: new HookSpec { Command = "hook.sh" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, mine, other);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [mine, other], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        installer.Remove(kind: null, ["guard"], providers: null, InstallScope.Project);

        // No dangling registration pointing at the deleted script; the other hook survives.
        var settings = File.ReadAllText(Path.Combine(paths.WorkingDirectory, ".claude", "settings.json"));
        Assert.DoesNotContain("guard", settings);
        Assert.Contains("keeper", settings);
        Assert.False(Directory.Exists(Path.Combine(paths.WorkingDirectory, ".claude", "hooks", "guard")));
    }

    [Fact]
    public void RemovingAnMcpServerDeletesItsEntryAndKeepsUserEntries()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        File.WriteAllText(Path.Combine(paths.WorkingDirectory, ".mcp.json"),
            """{ "mcpServers": { "mine": { "type": "stdio", "command": "my-server" } } }""");
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "github",
            mcp: new McpServer { Server = "github", Command = "github-mcp" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        installer.Remove(kind: null, ["github"], providers: null, InstallScope.Project);

        var mcpJson = File.ReadAllText(Path.Combine(paths.WorkingDirectory, ".mcp.json"));
        Assert.DoesNotContain("github", mcpJson);
        Assert.Contains("mine", mcpJson);
    }

    [Fact]
    public void RemovingACodexMcpServerDeletesItsTomlSection()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var configPath = Path.Combine(paths.WorkingDirectory, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, "model = \"o4\"\n\n[mcp_servers.mine]\ncommand = \"my-server\"\n");

        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "github",
            mcp: new McpServer { Server = "github", Command = "github-mcp" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [asset], [ProviderName.Codex], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        installer.Remove(kind: null, ["github"], providers: null, InstallScope.Project);

        var toml = File.ReadAllText(configPath);
        Assert.DoesNotContain("[mcp_servers.github]", toml);
        Assert.Contains("[mcp_servers.mine]", toml);
        Assert.Contains("model = \"o4\"", toml);
    }
}

public class UnmanagedFileProtectionTests
{
    [Fact]
    public void UnmanagedFileIsKeptUnlessTheDriftDecisionSaysOverwrite()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Instructions, "org-instructions",
            files: new Dictionary<string, string> { ["org-instructions.md"] = "# Org rules\n" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);

        var claudeMd = Path.Combine(paths.WorkingDirectory, "CLAUDE.md");
        File.WriteAllText(claudeMd, "# My hand-written instructions\n");

        var plan = installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.UnmanagedPresent, Assert.Single(plan.Items).State);

        var kept = installer.Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Keep);
        Assert.Equal(ApplyOutcome.KeptLocalChanges, Assert.Single(kept).Outcome);
        Assert.Equal("# My hand-written instructions\n", File.ReadAllText(claudeMd));

        var overwritten = installer.Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Overwrite);
        Assert.Equal(ApplyOutcome.Installed, Assert.Single(overwritten).Outcome);
        Assert.Equal("# Org rules\n", File.ReadAllText(claudeMd));
    }

    [Fact]
    public void ExistingSharedConfigIsNotUnmanagedForMergeInstalls()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var settingsPath = Path.Combine(paths.WorkingDirectory, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, """{ "model": "opus" }""");

        var hook = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard", hook: new HookSpec { Command = "hook.sh" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, hook);
        var installer = new Installer(paths);

        var plan = installer.Plan(loaded, [hook], [ProviderName.Claude], InstallScope.Project);
        Assert.Equal(InstallState.Available, Assert.Single(plan.Items).State);

        installer.Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Keep);
        var merged = File.ReadAllText(settingsPath);
        Assert.Contains("\"model\"", merged);
        Assert.Contains("guard", merged);
    }
}

public class GitArgumentSafetyTests
{
    [Fact]
    public void BranchNamesThatLookLikeGitOptionsAreRejected()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var sources = new SourceManager(paths);

        var ex = Assert.Throws<AgentPackException>(() => sources.Sync(new AgentPackSource
        {
            Name = "evil",
            Url = "https://example.com/repo.git",
            Branch = "--upload-pack=touch pwned"
        }));
        Assert.Contains("branch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UrlsThatLookLikeGitOptionsAreRejected()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var sources = new SourceManager(paths);

        Assert.Throws<AgentPackException>(() => sources.Sync(new AgentPackSource
        {
            Name = "evil",
            Url = "--upload-pack=touch pwned",
            Branch = "main"
        }));
    }
}

public class ProviderHomeTests
{
    [Fact]
    public void CustomAgentPackHomeDoesNotRelocateProviderConfigs()
    {
        using var temp = new TempDir();
        var customHome = Path.Combine(temp.Path, "opt", "agentpack-state");
        var paths = new AgentPackPaths(customHome, temp.Path);

        // Provider configs (.claude/, .codex/, ...) belong in the real user profile,
        // not in the parent of wherever AGENTPACK_HOME points.
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), paths.ProviderHome);
        Assert.Equal(customHome, paths.Home);
    }
}

public class UserConfigParsingTests
{
    [Fact]
    public void CorruptSharedConfigFailsWithActionableErrorNotInternalError()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var settingsPath = Path.Combine(paths.WorkingDirectory, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, "{ not json at all");

        var hook = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard", hook: new HookSpec { Command = "hook.sh" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, hook);
        var installer = new Installer(paths);

        var ex = Assert.Throws<AgentPackException>(() =>
            installer.Apply(installer.Plan(loaded, [hook], [ProviderName.Claude], InstallScope.Project).Items,
                loaded, InstallScope.Project, _ => DriftAction.Keep));
        Assert.Contains("settings.json", ex.Message);
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void JsoncCommentsInVsCodeMcpJsonAreTolerated()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".vscode", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, """
            {
              // my servers
              "servers": {
                "mine": { "type": "stdio", "command": "my-server" },
              }
            }
            """);

        var asset = TestData.Asset(AssetKind.Mcp, "github", mcp: new McpServer { Server = "github", Command = "github-mcp" });
        var target = new InstallTarget(ProviderName.Copilot, AssetKind.Mcp, Path.Combine(".vscode", "mcp.json"), InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        var output = File.ReadAllText(targetPath);
        Assert.Contains("\"mine\"", output);
        Assert.Contains("\"github\"", output);
    }
}

public class BackupPruningTests
{
    [Fact]
    public void OldBackupsArePrunedToTheNewestTwenty()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var backupsRoot = Path.Combine(paths.WorkingDirectory, ".agentpack", "backups");
        for (var i = 0; i < 30; i++)
        {
            Directory.CreateDirectory(Path.Combine(backupsRoot, $"2026010100{i:D4}000"));
        }

        var asset = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Skills, "demo");
        var loaded = TestData.Loaded(paths.WorkingDirectory, asset);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        // Trigger a backup by overwriting the modified install.
        File.WriteAllText(Path.Combine(paths.WorkingDirectory, ".claude", "skills", "demo", "SKILL.md"), "edited\n");
        var plan = installer.Plan(loaded, [asset], [ProviderName.Claude], InstallScope.Project);
        installer.Apply(plan.Items, loaded, InstallScope.Project, _ => DriftAction.Overwrite);

        Assert.True(Directory.EnumerateDirectories(backupsRoot).Count() <= 20,
            "backups must be pruned to the newest 20");
    }
}

public class LockfileMigrationTests
{
    [Fact]
    public void LegacyEntryGetsItsFragmentBackfilledOnTheNextApply()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var hook = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Hooks, "guard", hook: new HookSpec { Command = "hook.sh" });
        var mcp = TestData.WriteLocalAsset(paths.WorkingDirectory, AssetKind.Mcp, "github",
            mcp: new McpServer { Server = "github", Command = "github-mcp" });
        var loaded = TestData.Loaded(paths.WorkingDirectory, hook, mcp);
        var installer = new Installer(paths);
        installer.Apply(installer.Plan(loaded, [hook, mcp], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);

        // Simulate a lockfile written by agentpack < 1.0: no fragment, whole-file checksum.
        var lockFile = JsonStore.Load<AgentPackLock>(paths.ProjectLockPath);
        foreach (var entry in lockFile.Entries)
        {
            entry.Fragment = null;
            entry.InstalledChecksum = ContentHash.Compute(
                Installer.ResolveLockPath(entry.Path, paths.WorkingDirectory));
        }

        JsonStore.Save(paths.ProjectLockPath, lockFile);

        var results = installer.Apply(installer.Plan(loaded, [hook, mcp], [ProviderName.Claude], InstallScope.Project).Items,
            loaded, InstallScope.Project, _ => DriftAction.Keep);
        Assert.All(results, r => Assert.Equal(ApplyOutcome.AlreadyUpToDate, r.Outcome));

        var migrated = JsonStore.Load<AgentPackLock>(paths.ProjectLockPath);
        Assert.All(migrated.Entries, entry => Assert.NotNull(entry.Fragment));

        // After the backfill, removal un-merges correctly again.
        installer.Remove(kind: null, ["guard"], providers: null, InstallScope.Project);
        Assert.DoesNotContain("guard", File.ReadAllText(Path.Combine(paths.WorkingDirectory, ".claude", "settings.json")));
    }
}

public class LockfileForwardCompatTests
{
    [Fact]
    public void UnknownFieldsSurviveALoadSaveRoundTrip()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "lock.json");
        File.WriteAllText(path, """
            {
              "entries": [
                {
                  "id": "demo",
                  "kind": "skills",
                  "provider": "claude",
                  "version": "1.0.0",
                  "path": ".claude/skills/demo",
                  "futureEntryField": { "nested": true }
                }
              ],
              "futureTopLevelField": "keep me"
            }
            """);

        var loaded = JsonStore.Load<AgentPackLock>(path);
        JsonStore.Save(path, loaded);

        var written = File.ReadAllText(path);
        Assert.Contains("futureTopLevelField", written);
        Assert.Contains("keep me", written);
        Assert.Contains("futureEntryField", written);
    }
}

public class CatalogStalenessTests
{
    [Fact]
    public void FreshlySyncedSourceIsNotRefreshed()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var sources = new SourceManager(paths);
        sources.UseSource("org", "https://example.com/unreachable.git");

        var cachePath = sources.SourceCachePath(new AgentPackSource { Name = "org" });
        Directory.CreateDirectory(cachePath);
        File.WriteAllText(cachePath + ".synced", DateTimeOffset.UtcNow.ToString("O"));

        Assert.Null(sources.RefreshIfStale(Path.Combine(cachePath, "catalog.yaml")));
    }

    [Fact]
    public void StaleSourceThatCannotSyncWarnsInsteadOfFailing()
    {
        using var temp = new TempDir();
        var paths = TestData.Paths(temp);
        var sources = new SourceManager(paths);
        sources.UseSource("org", "https://example.invalid/unreachable.git");

        var cachePath = sources.SourceCachePath(new AgentPackSource { Name = "org" });
        Directory.CreateDirectory(cachePath);
        File.WriteAllText(Path.Combine(cachePath, "catalog.yaml"), "schemaVersion: \"1\"\ncatalogVersion: 0.1.0\n");
        // No .synced marker: the source counts as never refreshed.

        var warning = sources.RefreshIfStale(Path.Combine(cachePath, "catalog.yaml"));
        Assert.NotNull(warning);
        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.Contains("cached copy", warning.Message);
    }
}
