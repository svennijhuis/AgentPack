using System.Diagnostics;
using AgentPack.Core;

namespace AgentPack.Tests;

/// <summary>
/// End-to-end tests running the real CLI binary against a temp catalog repo.
/// </summary>
public class CliEndToEndTests
{
    [Fact]
    public void HelpSucceeds()
    {
        using var temp = new TempDir();
        var result = RunCli(temp, "--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("agentpack", result.Output);
        Assert.Contains("install", result.Output);
        Assert.Contains("submit", result.Output);
    }

    [Fact]
    public void NoArgumentsShowsTaskOrientedGettingStartedScreen()
    {
        using var temp = new TempDir();
        var result = RunCli(temp);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Install from the catalog", result.Output);
        Assert.Contains("agentpack install <id> --user", result.Output);
        Assert.Contains("agentpack submit <kind> <path-or-url-or-id>", result.Output);
        Assert.Contains("agentpack search <query>", result.Output);
        Assert.DoesNotContain("USAGE:", result.Output);
    }

    [Fact]
    public void HelpCommandSupportsRootCommandsAndNestedCommands()
    {
        using var temp = new TempDir();

        var root = RunCli(temp, "help");
        Assert.Equal(0, root.ExitCode);
        Assert.Contains("COMMANDS:", root.Output);

        var install = RunCli(temp, "help", "install");
        Assert.Equal(0, install.ExitCode);
        Assert.Contains("agentpack install", install.Output);

        var submit = RunCli(temp, "help", "submit");
        Assert.Equal(0, submit.ExitCode);
        Assert.Contains("agentpack submit", submit.Output);

        var nested = RunCli(temp, "help", "profile", "apply");
        Assert.Equal(0, nested.ExitCode);
        Assert.Contains("agentpack profile apply", nested.Output);
    }

    [Fact]
    public void FindSearchesOnlyApprovedEffectiveCatalogMetadata()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "service-setup", description: "Bootstrap TypeScript backend services.", groups: "[backend, platform]");
        WriteSkill(temp, "frontend-review", description: "Review React user interfaces.", groups: "[review]");

        var byWords = RunCli(temp, "search", "typescript service");
        Assert.Equal(0, byWords.ExitCode);
        Assert.Contains("service-setup", byWords.Output);
        Assert.DoesNotContain("frontend-review", byWords.Output);

        var byFilter = RunCli(temp, "search", "review", "--kind", "skills", "--group", "review", "--provider", "codex");
        Assert.Equal(0, byFilter.ExitCode);
        Assert.Contains("frontend-review", byFilter.Output);
        Assert.DoesNotContain("service-setup", byFilter.Output);

        var none = RunCli(temp, "search", "not-in-approved-catalog");
        Assert.Equal(0, none.ExitCode);
        Assert.Contains("No approved assets match", none.Output);
    }

    [Fact]
    public void InstallUsesTheApprovedCatalog()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        Assert.Contains("demo-skill", RunCli(temp, "list").Output);
        var install = RunCli(temp, "install", "demo-skill", "--claude", "--project", "--yes");
        Assert.Equal(0, install.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "demo-skill", "SKILL.md")));
    }

    [Fact]
    public void InstallDryRunShowsDestinationWithoutWritingFiles()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "install", "demo-skill", "--claude", "--project", "--dry-run");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Catalog: local", result.Output);
        Assert.Contains("project scope", result.Output);
        Assert.Contains("Install plan (dry run)", result.Output);
        Assert.False(Directory.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "demo-skill")));
    }

    [Fact]
    public void CatalogStatusShowsBuiltInCatalogWithoutDownloadingIt()
    {
        using var temp = new TempDir();

        var result = RunCli(temp, "catalog", "status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("official", result.Output);
        Assert.Contains(AgentPackDefaults.OfficialCatalogUrl, result.Output);
        Assert.Contains("not downloaded", result.Output);
    }

    [Fact]
    public void CatalogCanRequireANewerAgentPackVersion()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        File.AppendAllText(Path.Combine(WorkDir(temp), "catalog.yaml"), "\nminimumAgentPackVersion: 99.0.0\n");

        var result = RunCli(temp, "list");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("requires AgentPack 99.0.0 or newer", result.Output + result.Error);
        Assert.Contains("dotnet tool update -g AgentPack", result.Output + result.Error);
    }

    [Fact]
    public void SubmitPrepareOnlyCreatesAValidatedProposalBranchWithoutChangingMain()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "existing-skill");
        InitializeGitRepo(WorkDir(temp), "Initial catalog");

        var input = Path.Combine(temp.Path, "proposed-skill");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "SKILL.md"), "# Proposed skill\n");
        var environment = new Dictionary<string, string>
        {
            ["AGENTPACK_CATALOG_URL"] = WorkDir(temp),
            ["GIT_AUTHOR_NAME"] = "AgentPack Tests",
            ["GIT_AUTHOR_EMAIL"] = "agentpack-tests@example.invalid",
            ["GIT_COMMITTER_NAME"] = "AgentPack Tests",
            ["GIT_COMMITTER_EMAIL"] = "agentpack-tests@example.invalid"
        };

        var result = RunCliWithEnvironment(temp, environment,
            "submit", "skill", input, "--description", "A proposed test skill.", "--prepare-only");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Prepared 'proposed-skill'", result.Output);
        Assert.Contains("Nothing was pushed", result.Output);
        Assert.False(Directory.Exists(Path.Combine(WorkDir(temp), "assets", "skills", "proposed-skill")));

        var submissions = Path.Combine(temp.Path, "home", ".agentpack", "submissions");
        var checkout = Assert.Single(Directory.GetDirectories(submissions));
        Assert.True(File.Exists(Path.Combine(checkout, "assets", "skills", "proposed-skill", "content", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(checkout, "catalog.lock.yaml")));
        var branch = ProcessRunner.Run("git", ["branch", "--show-current"], checkout);
        Assert.Equal(0, branch.ExitCode);
        Assert.StartsWith("agentpack/submit/proposed-skill-", branch.Output.Trim());
    }

    [Fact]
    public void SubmitLocalFolderCopiesOnlyThePreviewedSafeFiles()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        InitializeGitRepo(WorkDir(temp), "Initial catalog");

        var input = Path.Combine(temp.Path, "folder-skill");
        Directory.CreateDirectory(Path.Combine(input, "references"));
        Directory.CreateDirectory(Path.Combine(input, "node_modules", "not-published"));
        File.WriteAllText(Path.Combine(input, "SKILL.md"), "# Folder skill\n");
        File.WriteAllText(Path.Combine(input, "references", "guide.md"), "# Guide\n");
        File.WriteAllText(Path.Combine(input, "node_modules", "not-published", "secret.txt"), "ignored\n");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "skill", input, "--prepare-only");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Files to include: 2", result.Output);
        Assert.Contains("ignored: node_modules/", result.Output);
        var checkout = Assert.Single(Directory.GetDirectories(Path.Combine(temp.Path, "home", ".agentpack", "submissions")));
        var content = Path.Combine(checkout, "assets", "skills", "folder-skill", "content");
        Assert.True(File.Exists(Path.Combine(content, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(content, "references", "guide.md")));
        Assert.False(Directory.Exists(Path.Combine(content, "node_modules")));
    }

    [Fact]
    public void SubmitRejectsSecretLikeFilesBeforeCreatingABranch()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        InitializeGitRepo(WorkDir(temp), "Initial catalog");
        var input = Path.Combine(temp.Path, "unsafe-skill");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "SKILL.md"), "# Unsafe\n");
        File.WriteAllText(Path.Combine(input, ".env"), "TOKEN=real-secret\n");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "skill", input, "--prepare-only");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("secret-like file: .env", result.Output + result.Error);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "home", ".agentpack", "submissions")));
    }

    [Fact]
    public void SubmitRejectsSymlinksBeforeCreatingABranch()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new TempDir();
        WriteCatalog(temp);
        InitializeGitRepo(WorkDir(temp), "Initial catalog");
        var input = Path.Combine(temp.Path, "linked-skill");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "SKILL.md"), "# Linked\n");
        var outside = Path.Combine(temp.Path, "outside.md");
        File.WriteAllText(outside, "must not be copied\n");
        File.CreateSymbolicLink(Path.Combine(input, "linked.md"), outside);

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "skill", input, "--prepare-only");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("'linked.md' is a symlink", result.Output + result.Error);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "home", ".agentpack", "submissions")));
    }

    [Fact]
    public void SubmitHookAcceptsASingleScriptFileAndInfersItsCommand()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        InitializeGitRepo(WorkDir(temp), "Initial catalog");
        var script = Path.Combine(temp.Path, "check-secrets.sh");
        File.WriteAllText(script, "#!/bin/sh\nexit 0\n");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "hook", script, "--tool", "Bash", "--prepare-only");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Hook: preToolUse -> check-secrets.sh (30s)", result.Output);
        var checkout = Assert.Single(Directory.GetDirectories(Path.Combine(temp.Path, "home", ".agentpack", "submissions")));
        var manifest = File.ReadAllText(Path.Combine(checkout, "assets", "hooks", "check-secrets", "agentpack.yaml"));
        Assert.Contains("command: 'check-secrets.sh'", manifest);
        Assert.True(File.Exists(Path.Combine(checkout, "assets", "hooks", "check-secrets", "content", "check-secrets.sh")));
    }

    [Fact]
    public void SubmitHookFolderRequiresCommandWhenEntryIsAmbiguous()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        InitializeGitRepo(WorkDir(temp), "Initial catalog");
        var input = Path.Combine(temp.Path, "ambiguous-hook");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "first.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(input, "second.sh"), "#!/bin/sh\n");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "hook", input, "--prepare-only");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("more than one possible entry file", result.Output + result.Error);
        Assert.Contains("--command", result.Output + result.Error);
    }

    [Fact]
    public void SubmitSingleFileKindRejectsAnAmbiguousFolder()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        InitializeGitRepo(WorkDir(temp), "Initial catalog");
        var input = Path.Combine(temp.Path, "prompt-folder");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "prompt.md"), "Do the work.\n");
        File.WriteAllText(Path.Combine(input, "notes.md"), "Author notes.\n");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "prompt", input, "--prepare-only");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("installs as one provider-native file", result.Output + result.Error);
        Assert.Contains("Use a skill", result.Output + result.Error);
    }

    [Fact]
    public void SubmitMcpCreatesTypedMetadataWithoutSecretValuesOrPlaceholders()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        InitializeGitRepo(WorkDir(temp), "Initial catalog");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "mcp", "github", "--command", "github-mcp-server",
            "--arg", "stdio", "--env", "GITHUB_TOKEN", "--prepare-only");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("MCP: stdio -> github-mcp-server", result.Output);
        var checkout = Assert.Single(Directory.GetDirectories(Path.Combine(temp.Path, "home", ".agentpack", "submissions")));
        var manifest = File.ReadAllText(Path.Combine(checkout, "assets", "mcp", "github", "agentpack.yaml"));
        Assert.Contains("command: 'github-mcp-server'", manifest);
        Assert.Contains("envVars: ['GITHUB_TOKEN']", manifest);
        Assert.DoesNotContain("replace-me", manifest);
    }

    [Fact]
    public void SubmitMcpRejectsSecretValuesBeforeCreatingABranch()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        InitializeGitRepo(WorkDir(temp), "Initial catalog");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "mcp", "github", "--command", "github-mcp-server",
            "--env", "GITHUB_TOKEN=secret", "--prepare-only");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("expects an environment variable name", result.Output + result.Error);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "home", ".agentpack", "submissions")));
    }

    [Fact]
    public void SubmitAutomaticallyPinsAnExternalRepositoryToItsCurrentCommit()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        InitializeGitRepo(WorkDir(temp), "Initial catalog");

        var upstream = Path.Combine(temp.Path, "upstream-skill.git");
        Directory.CreateDirectory(upstream);
        File.WriteAllText(Path.Combine(upstream, "SKILL.md"), "# External skill\n");
        InitializeGitRepo(upstream, "Initial external skill");
        var expectedRef = ProcessRunner.Run("git", ["rev-parse", "HEAD"], upstream).Output.Trim();
        var environment = GitEnvironment(WorkDir(temp));

        var result = RunCliWithEnvironment(temp, environment,
            "submit", "skill", upstream, "--description", "An external test skill.", "--prepare-only");

        Assert.True(result.ExitCode == 0, result.Output + Environment.NewLine + result.Error);
        Assert.Contains($"Pinned external source at {expectedRef}", result.Output);
        var submissions = Path.Combine(temp.Path, "home", ".agentpack", "submissions");
        var checkout = Assert.Single(Directory.GetDirectories(submissions));
        var manifest = File.ReadAllText(Path.Combine(checkout, "assets", "skills", "upstream-skill", "agentpack.yaml"));
        Assert.Contains($"url: '{upstream}'", manifest);
        Assert.Contains($"ref: '{expectedRef}'", manifest);
    }

    [Fact]
    public void SubmitUpdateReplacesTheAssetAndBumpsItsVersion()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "existing-skill");
        // The published revision ships two files; the new one drops the second.
        var published = Path.Combine(WorkDir(temp), "assets", "skills", "existing-skill", "content");
        File.WriteAllText(Path.Combine(published, "legacy.md"), "# Legacy\n");
        InitializeGitRepo(WorkDir(temp), "Initial catalog");

        var input = Path.Combine(temp.Path, "existing-skill");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "SKILL.md"), "# Revised skill\n");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "skill", input, "--update", "--prepare-only");

        Assert.True(result.ExitCode == 0, result.Output + Environment.NewLine + result.Error);
        Assert.Contains("update existing-skill", result.Output);
        var checkout = Assert.Single(Directory.GetDirectories(Path.Combine(temp.Path, "home", ".agentpack", "submissions")));
        var content = Path.Combine(checkout, "assets", "skills", "existing-skill", "content");
        Assert.Equal("# Revised skill\n", File.ReadAllText(Path.Combine(content, "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(content, "legacy.md")));

        var manifest = File.ReadAllText(Path.Combine(checkout, "assets", "skills", "existing-skill", "agentpack.yaml"));
        Assert.Contains("version: '1.0.1'", manifest);

        // The removal must be part of the proposal, not just of the working tree.
        var tracked = ProcessRunner.Run("git", ["ls-files", "--", "assets/skills/existing-skill"], checkout);
        Assert.DoesNotContain("legacy.md", tracked.Output);
        var pending = ProcessRunner.Run("git", ["status", "--porcelain"], checkout);
        Assert.Equal("", pending.Output.Trim());
    }

    [Fact]
    public void SubmitRefusesAnExistingAssetWithoutUpdateBeforeCloning()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "existing-skill");
        InitializeGitRepo(WorkDir(temp), "Initial catalog");

        var input = Path.Combine(temp.Path, "existing-skill");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "SKILL.md"), "# Revised skill\n");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "skill", input, "--prepare-only");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("already in the catalog", result.Output + result.Error);
        Assert.Contains("--update", result.Output + result.Error);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "home", ".agentpack", "submissions")));
    }

    [Fact]
    public void SubmitUpdateRefusesAnAssetThatIsNotInTheCatalogYet()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        InitializeGitRepo(WorkDir(temp), "Initial catalog");

        var input = Path.Combine(temp.Path, "brand-new");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "SKILL.md"), "# New\n");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "skill", input, "--update", "--prepare-only");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("nothing to update", result.Output + result.Error);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "home", ".agentpack", "submissions")));
    }

    [Fact]
    public void SubmitUpdateRejectsAVersionThatIsNotNewerThanTheCatalog()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "existing-skill");
        InitializeGitRepo(WorkDir(temp), "Initial catalog");

        var input = Path.Combine(temp.Path, "existing-skill");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "SKILL.md"), "# Revised\n");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "skill", input, "--update", "--version", "1.0.0", "--prepare-only");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not newer than", result.Output + result.Error);
        Assert.Contains("--version 1.0.1", result.Output + result.Error);
    }

    /// <summary>An unrelated asset's checksum must not ride along in a one-asset proposal.</summary>
    [Fact]
    public void SubmitOnlyLocksTheSubmittedAsset()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "untouched-skill");
        InitializeGitRepo(WorkDir(temp), "Initial catalog");
        Assert.Equal(0, RunCli(temp, "catalog", "lock").ExitCode);
        var lockedBefore = File.ReadAllText(Path.Combine(WorkDir(temp), "catalog.lock.yaml"));
        InitializeGitCommit(WorkDir(temp), "Lock catalog");

        var input = Path.Combine(temp.Path, "added-skill");
        Directory.CreateDirectory(input);
        File.WriteAllText(Path.Combine(input, "SKILL.md"), "# Added\n");

        var result = RunCliWithEnvironment(temp, GitEnvironment(WorkDir(temp)),
            "submit", "skill", input, "--prepare-only");

        Assert.True(result.ExitCode == 0, result.Output + Environment.NewLine + result.Error);
        var checkout = Assert.Single(Directory.GetDirectories(Path.Combine(temp.Path, "home", ".agentpack", "submissions")));
        var lockedAfter = File.ReadAllText(Path.Combine(checkout, "catalog.lock.yaml"));
        Assert.Contains("added-skill", lockedAfter);
        foreach (var line in lockedBefore.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.Contains(line.Trim(), lockedAfter);
        }
    }

    [Fact]
    public void VersionFlagPrintsTheToolVersion()
    {
        using var temp = new TempDir();
        var result = RunCli(temp, "--version");
        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"\d+\.\d+\.\d+", result.Output);
    }

    [Fact]
    public void UnknownCommandSuggestsNearestAndFails()
    {
        using var temp = new TempDir();
        var result = RunCli(temp, "statsu");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Did you mean 'agentpack status'?", result.Output + result.Error);
    }

    [Fact]
    public void InstallInstallsAndStatusReportsIt()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");
        Directory.CreateDirectory(Path.Combine(WorkDir(temp), ".claude"));

        var install = RunCli(temp, "install", "demo-skill", "--claude", "--project", "--yes");
        Assert.Equal(0, install.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "demo-skill", "SKILL.md")));

        var status = RunCli(temp, "status", "--project");
        Assert.Contains("demo-skill", status.Output);
    }

    [Fact]
    public void SkillExtrasLikeOpenaiYamlSurviveInstallToEveryProvider()
    {
        // Skills may ship optional per-tool extras (e.g. Codex's agents/openai.yaml
        // with desktop-app UI metadata and invocation policy). agentpack copies the
        // skill tree byte-for-byte — it never strips or generates these files.
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "code-review");
        var extrasDir = Path.Combine(WorkDir(temp), "assets", "skills", "code-review", "content", "agents");
        Directory.CreateDirectory(extrasDir);
        File.WriteAllText(Path.Combine(extrasDir, "openai.yaml"), "display_name: Code Review\ninterface:\n  icon: magnifying-glass\n");

        var install = RunCli(temp, "install", "code-review", "--claude", "--cursor", "--copilot", "--codex", "--project", "--yes");
        Assert.Equal(0, install.ExitCode);
        string[] skillRoots =
        [
            Path.Combine(".claude", "skills", "code-review"),
            Path.Combine(".agents", "skills", "code-review"),
            Path.Combine(".github", "skills", "code-review"),
            Path.Combine(".cursor", "skills", "code-review")
        ];
        foreach (var root in skillRoots)
        {
            Assert.True(File.Exists(Path.Combine(WorkDir(temp), root, "SKILL.md")), $"SKILL.md missing under {root}");
            Assert.True(File.Exists(Path.Combine(WorkDir(temp), root, "agents", "openai.yaml")), $"agents/openai.yaml missing under {root}");
        }

        var remove = RunCli(temp, "remove", "code-review", "--project", "--yes");
        Assert.Equal(0, remove.ExitCode);
        foreach (var root in skillRoots)
        {
            Assert.False(Directory.Exists(Path.Combine(WorkDir(temp), root)), $"{root} should be removed");
        }
    }

    [Fact]
    public void AgentInstallsEveryProviderFormat()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var asset = Path.Combine(WorkDir(temp), "assets", "agents", "code-reviewer");
        Directory.CreateDirectory(Path.Combine(asset, "content"));
        File.WriteAllText(Path.Combine(asset, "content", "AGENT.md"),
            "---\nname: code-reviewer\ndescription: Review code.\n---\n\nReview the code.\n");
        File.WriteAllText(Path.Combine(asset, "agentpack.yaml"),
            "name: Code Reviewer\nversion: 1.0.0\ndescription: Review code.\ngroups: [review]\n");

        var install = RunCli(temp, "install", "code-reviewer", "--claude", "--cursor", "--copilot", "--codex", "--project", "--yes");
        Assert.Equal(0, install.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "agents", "code-reviewer.md")));
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".cursor", "agents", "code-reviewer.md")));
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".github", "agents", "code-reviewer.agent.md")));

        var codexToml = File.ReadAllText(Path.Combine(WorkDir(temp), ".codex", "agents", "code-reviewer.toml"));
        Assert.Contains("name = \"code-reviewer\"", codexToml);
        Assert.Contains("developer_instructions = \"\"\"", codexToml);
    }

    [Fact]
    public void InstallRuleInstallsCursorMdcAndClaudeTranslation()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var dir = Path.Combine(WorkDir(temp), "assets", "rules", "ts-style");
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "content", "ts-style.mdc"),
            "---\ndescription: TS rules.\nglobs: \"*.ts\"\n---\n\nPrefer explicit return types.\n");
        File.WriteAllText(Path.Combine(dir, "agentpack.yaml"),
            "name: TS Style\nversion: 1.0.0\ndescription: Test rule.\ngroups: [review]\n");

        var install = RunCli(temp, "install", "ts-style", "--claude", "--cursor", "--project", "--yes");
        Assert.Equal(0, install.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".cursor", "rules", "ts-style.mdc")));

        var claudeRule = File.ReadAllText(Path.Combine(WorkDir(temp), ".claude", "rules", "ts-style.md"));
        Assert.Contains("paths:", claudeRule);
        Assert.Contains("\"*.ts\"", claudeRule);
        Assert.DoesNotContain("globs", claudeRule);

        // Re-adding is a no-op, and remove deletes both provider files.
        var again = RunCli(temp, "install", "ts-style", "--claude", "--cursor", "--project", "--yes");
        Assert.Equal(0, again.ExitCode);
        var remove = RunCli(temp, "remove", "ts-style", "--project", "--yes");
        Assert.Equal(0, remove.ExitCode);
        Assert.False(File.Exists(Path.Combine(WorkDir(temp), ".claude", "rules", "ts-style.md")));
        Assert.False(File.Exists(Path.Combine(WorkDir(temp), ".cursor", "rules", "ts-style.mdc")));
    }

    [Fact]
    public void InstallUnknownAssetSuggestsNearest()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "install", "demo-skil", "--claude", "--project", "--yes");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("demo-skill", result.Output + result.Error);
    }

    [Fact]
    public void InstallWithoutTerminalAndWithoutArgsGivesGuidance()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "install", "--claude", "--project");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("agentpack install code-review", result.Output + result.Error);
    }

    [Fact]
    public void InstallEverythingWithYesIsRefused()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "install", "--claude", "--project", "--yes");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("entire catalog", result.Output + result.Error);
    }

    [Fact]
    public void InstallByKindInstallsAllOfKindWhenNonInteractive()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "skill-one");
        WriteSkill(temp, "skill-two");

        var result = RunCli(temp, "install", "skills", "--claude", "--project", "--yes");
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "skill-one", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "skill-two", "SKILL.md")));
    }

    [Fact]
    public void ListHidesSourceColumnForLocalOnlyCatalog()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var plain = RunCli(temp, "list");
        Assert.Equal(0, plain.ExitCode);
        Assert.Contains("demo-skill", plain.Output);
        Assert.DoesNotContain("Source", plain.Output);
    }

    [Fact]
    public void ConfigShowsHomeAndProviderPaths()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);

        var result = RunCli(temp, "config");

        Assert.Equal(0, result.ExitCode);
        // Long paths wrap at the default width, so assert on short unbroken tokens.
        Assert.Contains("home", result.Output);
        Assert.Contains("provider", result.Output);
        Assert.Contains("AGENTPACK_HOME", result.Output);
    }

    [Fact]
    public void StatusOnEmptyScopeExplainsNextStep()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "status", "--project");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Nothing installed", result.Output);
        Assert.Contains("agentpack install", result.Output);
    }

    [Fact]
    public void InstallDryRunCollapsesUpToDateRows()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var ids = new[] { "skill-aa", "skill-bb", "skill-cc", "skill-dd", "skill-ee", "skill-ff", "skill-gg" };
        foreach (var id in ids) WriteSkill(temp, id);

        Assert.Equal(0, RunCli(temp, "install", "skills", "--claude", "--project", "--yes").ExitCode);

        var plan = RunCli(temp, "install", "skills", "--claude", "--project", "--dry-run");
        Assert.Equal(0, plan.ExitCode);
        Assert.Contains("7 already up to date", plan.Output);
        Assert.Contains("not shown", plan.Output);
        Assert.DoesNotContain("skill-aa", plan.Output);
    }

    [Fact]
    public void ReapplyCollapsesAlreadyUpToDateResults()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var ids = new[] { "skill-aa", "skill-bb", "skill-cc", "skill-dd", "skill-ee", "skill-ff", "skill-gg" };
        foreach (var id in ids) WriteSkill(temp, id);
        Assert.Equal(0, RunCli(temp, "install", "skills", "--claude", "--project", "--yes").ExitCode);

        // One new asset makes the second install actionable; the 7 untouched installs collapse.
        WriteSkill(temp, "skill-new");
        var again = RunCli(temp, "install", "skills", "--claude", "--project", "--yes");
        Assert.Equal(0, again.ExitCode);
        Assert.Contains("installed skill-new", again.Output);
        Assert.Contains("7 already up to date", again.Output);
    }

    [Fact]
    public void CatalogLockThenValidatePasses()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        Assert.Equal(0, RunCli(temp, "catalog", "lock", "--no-fetch").ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), "catalog.lock.yaml")));

        var validate = RunCli(temp, "catalog", "validate");
        Assert.Equal(0, validate.ExitCode);

        // Content edit without relock: validation must fail on checksum mismatch.
        File.WriteAllText(Path.Combine(WorkDir(temp), "assets", "skills", "demo-skill", "content", "SKILL.md"), "changed\n");
        var stale = RunCli(temp, "catalog", "validate");
        Assert.NotEqual(0, stale.ExitCode);
        Assert.Contains("mismatch", (stale.Output + stale.Error).ToLowerInvariant());
    }

    private static string WorkDir(TempDir temp) => Path.Combine(temp.Path, "repo");

    private static void WriteCatalog(TempDir temp)
    {
        Directory.CreateDirectory(WorkDir(temp));
        File.WriteAllText(Path.Combine(WorkDir(temp), "catalog.yaml"), """
            schemaVersion: "1"
            catalogVersion: 0.1.0
            groups:
              - id: review
                name: Review
            """);
    }

    private static void WriteSkill(
        TempDir temp,
        string id,
        string? extraYaml = null,
        string description = "Test skill.",
        string groups = "[review]")
    {
        var dir = Path.Combine(WorkDir(temp), "assets", "skills", id);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "content", "SKILL.md"), $"# {id}\n");
        File.WriteAllText(Path.Combine(dir, "agentpack.yaml"),
            $"name: {id}\nversion: 1.0.0\ndescription: {description}\ngroups: {groups}\n{extraYaml}\n");
    }

    private static void InitializeGitRepo(string directory, string message)
    {
        Assert.Equal(0, ProcessRunner.Run("git", ["init", "-b", "main"], directory).ExitCode);
        Assert.Equal(0, ProcessRunner.Run("git", ["config", "user.name", "AgentPack Tests"], directory).ExitCode);
        Assert.Equal(0, ProcessRunner.Run("git", ["config", "user.email", "agentpack-tests@example.invalid"], directory).ExitCode);
        InitializeGitCommit(directory, message);
    }

    private static void InitializeGitCommit(string directory, string message)
    {
        Assert.Equal(0, ProcessRunner.Run("git", ["add", "."], directory).ExitCode);
        Assert.Equal(0, ProcessRunner.Run("git", ["commit", "-m", message], directory).ExitCode);
    }

    private static Dictionary<string, string> GitEnvironment(string catalogUrl) => new()
    {
        ["AGENTPACK_CATALOG_URL"] = catalogUrl,
        ["GIT_AUTHOR_NAME"] = "AgentPack Tests",
        ["GIT_AUTHOR_EMAIL"] = "agentpack-tests@example.invalid",
        ["GIT_COMMITTER_NAME"] = "AgentPack Tests",
        ["GIT_COMMITTER_EMAIL"] = "agentpack-tests@example.invalid"
    };

    private static (int ExitCode, string Output, string Error) RunCli(TempDir temp, params string[] args)
        => RunCliCore(temp, null, args);

    private static (int ExitCode, string Output, string Error) RunCliWithEnvironment(
        TempDir temp,
        IReadOnlyDictionary<string, string> environment,
        params string[] args)
        => RunCliCore(temp, environment, args);

    private static (int ExitCode, string Output, string Error) RunCliCore(
        TempDir temp,
        IReadOnlyDictionary<string, string>? environment,
        params string[] args)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "agentpack.dll");
        Assert.True(File.Exists(cliDll), $"CLI binary not found at {cliDll}");

        Directory.CreateDirectory(WorkDir(temp));
        var home = Path.Combine(temp.Path, "home", ".agentpack");
        Directory.CreateDirectory(home);

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = WorkDir(temp),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(cliDll);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["AGENTPACK_HOME"] = home;
        start.Environment["CI"] = "1";
        start.Environment["NO_COLOR"] = "1";
        if (environment is not null)
        {
            foreach (var (name, value) in environment) start.Environment[name] = value;
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "CLI process timed out");
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
