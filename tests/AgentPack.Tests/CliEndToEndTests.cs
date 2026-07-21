using System.Diagnostics;

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
        Assert.Contains("add", result.Output);
    }

    [Fact]
    public void NoArgumentsShowsTaskOrientedGettingStartedScreen()
    {
        using var temp = new TempDir();
        var result = RunCli(temp);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Use an existing team catalog", result.Output);
        Assert.Contains("agentpack init --overlay", result.Output);
        Assert.Contains("agentpack find <query>", result.Output);
        Assert.DoesNotContain("USAGE:", result.Output);
    }

    [Fact]
    public void HelpCommandSupportsRootCommandsAndNestedCommands()
    {
        using var temp = new TempDir();

        var root = RunCli(temp, "help");
        Assert.Equal(0, root.ExitCode);
        Assert.Contains("COMMANDS:", root.Output);

        var add = RunCli(temp, "help", "add");
        Assert.Equal(0, add.ExitCode);
        Assert.Contains("agentpack add", add.Output);
        Assert.Contains("--overlay", RunCli(temp, "help", "new").Output);

        var nested = RunCli(temp, "help", "profile", "apply");
        Assert.Equal(0, nested.ExitCode);
        Assert.Contains("agentpack profile apply", nested.Output);
    }

    [Fact]
    public void InitCreatesCatalogsAndNeverOverwritesThem()
    {
        using var rootTemp = new TempDir();
        var root = RunCli(rootTemp, "init");
        Assert.Equal(0, root.ExitCode);
        var rootCatalog = Path.Combine(WorkDir(rootTemp), "catalog.yaml");
        Assert.True(File.Exists(rootCatalog));
        var original = File.ReadAllText(rootCatalog);
        var repeated = RunCli(rootTemp, "init");
        Assert.NotEqual(0, repeated.ExitCode);
        Assert.Equal(original, File.ReadAllText(rootCatalog));

        using var overlayTemp = new TempDir();
        var overlay = RunCli(overlayTemp, "init", "--overlay");
        Assert.Equal(0, overlay.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(overlayTemp), ".agentpack", "catalog.yaml")));
        Assert.False(File.Exists(Path.Combine(WorkDir(overlayTemp), "catalog.yaml")));
    }

    [Fact]
    public void NewOverlayCreatesStandaloneProjectCatalogAndAsset()
    {
        using var temp = new TempDir();
        var create = RunCli(temp, "new", "skills", "service-setup", "--overlay");
        Assert.Equal(0, create.ExitCode);
        Assert.Contains(".agentpack/catalog.yaml", create.Output);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".agentpack", "catalog.yaml")));
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".agentpack", "assets", "skills", "service-setup", "content", "SKILL.md")));

        var list = RunCli(temp, "list");
        Assert.Equal(0, list.ExitCode);
        Assert.Contains("service-setup", list.Output);

        var validate = RunCli(temp, "catalog", "validate", "--no-checksums");
        Assert.Equal(0, validate.ExitCode);
    }

    [Fact]
    public void NewOverlayScaffoldsEveryInstallableAssetKindIntoAValidCatalog()
    {
        using var temp = new TempDir();
        // Tools and templates remain explicit unsupported kinds in the provider matrix;
        // scaffolding them is allowed for forward compatibility, but validation correctly
        // rejects catalogs that claim they can currently be installed.
        string[] kinds = ["skills", "hooks", "mcp", "instructions", "rules", "prompts", "agents"];
        foreach (var kind in kinds)
        {
            var create = RunCli(temp, "new", kind, $"sample-{kind}", "--overlay");
            Assert.Equal(0, create.ExitCode);
            Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".agentpack", "assets", kind, $"sample-{kind}", "agentpack.yaml")));
        }

        var validate = RunCli(temp, "catalog", "validate", "--no-checksums");
        Assert.Equal(0, validate.ExitCode);
    }

    [Fact]
    public void FindSearchesOnlyApprovedEffectiveCatalogMetadata()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "service-setup", description: "Bootstrap TypeScript backend services.", groups: "[backend, platform]");
        WriteSkill(temp, "frontend-review", description: "Review React user interfaces.", groups: "[review]");

        var byWords = RunCli(temp, "find", "typescript service");
        Assert.Equal(0, byWords.ExitCode);
        Assert.Contains("service-setup", byWords.Output);
        Assert.DoesNotContain("frontend-review", byWords.Output);

        var byFilter = RunCli(temp, "search", "review", "--kind", "skills", "--group", "review", "--provider", "codex");
        Assert.Equal(0, byFilter.ExitCode);
        Assert.Contains("frontend-review", byFilter.Output);
        Assert.DoesNotContain("service-setup", byFilter.Output);

        var none = RunCli(temp, "find", "not-in-approved-catalog");
        Assert.Equal(0, none.ExitCode);
        Assert.Contains("No approved assets match", none.Output);
    }

    [Fact]
    public void FamiliarAliasesRemainEquivalent()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        Assert.Contains("demo-skill", RunCli(temp, "ls").Output);
        var install = RunCli(temp, "install", "demo-skill", "--claude", "--project", "--yes");
        Assert.Equal(0, install.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "demo-skill", "SKILL.md")));
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
    public void NewScaffoldsManifestAndContent()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var result = RunCli(temp, "new", "skills", "grill-me", "--group", "review");
        Assert.Equal(0, result.ExitCode);

        var manifest = Path.Combine(WorkDir(temp), "assets", "skills", "grill-me", "agentpack.yaml");
        Assert.True(File.Exists(manifest));
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), "assets", "skills", "grill-me", "content", "SKILL.md")));
        var yaml = File.ReadAllText(manifest);
        Assert.Contains("name: Grill Me", yaml);
        Assert.DoesNotContain("checksum", yaml);

        // A second run without --force refuses to overwrite.
        var again = RunCli(temp, "new", "skills", "grill-me");
        Assert.NotEqual(0, again.ExitCode);
        Assert.Contains("--force", again.Output + again.Error);
    }

    [Fact]
    public void ImportRequiresPinnedRef()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);

        var floating = RunCli(temp, "import", "https://github.com/example/skills/tree/main/skills/pdf");
        Assert.NotEqual(0, floating.ExitCode);
        Assert.Contains("pin", (floating.Output + floating.Error).ToLowerInvariant());

        var pinned = RunCli(temp, "import",
            "https://github.com/example/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d");
        Assert.Equal(0, pinned.ExitCode);
        var manifest = File.ReadAllText(Path.Combine(WorkDir(temp), "assets", "skills", "pdf", "agentpack.yaml"));
        Assert.Contains("url: https://github.com/example/skills/tree/main/skills/pdf", manifest);
        Assert.Contains("ref: 9d2f1ae187231d8199c64b5b762e1bdf2244733d", manifest);
        Assert.DoesNotContain("license:", manifest);
        Assert.Equal(0, RunCli(temp, "catalog", "validate", "--no-checksums").ExitCode);

        var plan = RunCli(temp, "plan", "pdf", "--claude", "--project");
        Assert.Equal(0, plan.ExitCode);
        Assert.Contains("Source", plan.Output);
        Assert.Contains("example/skills", plan.Output);
    }

    [Fact]
    public void ImportCanRecordAnOptionalLicense()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        const string source = "https://github.com/example/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d";

        var result = RunCli(temp, "import", source, "--license", "MIT");

        Assert.Equal(0, result.ExitCode);
        var manifest = File.ReadAllText(Path.Combine(WorkDir(temp), "assets", "skills", "pdf", "agentpack.yaml"));
        Assert.Contains("license: MIT", manifest);
    }

    [Fact]
    public void AddInstallsAndStatusReportsIt()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");
        Directory.CreateDirectory(Path.Combine(WorkDir(temp), ".claude"));

        var add = RunCli(temp, "add", "demo-skill", "--claude", "--project", "--yes");
        Assert.Equal(0, add.ExitCode);
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

        var add = RunCli(temp, "add", "code-review", "--claude", "--cursor", "--copilot", "--codex", "--project", "--yes");
        Assert.Equal(0, add.ExitCode);
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
    public void NewAgentThenAddInstallsEveryProviderFormat()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);

        var scaffold = RunCli(temp, "new", "agents", "code-reviewer", "--group", "review");
        Assert.Equal(0, scaffold.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), "assets", "agents", "code-reviewer", "content", "AGENT.md")));

        var add = RunCli(temp, "add", "code-reviewer", "--claude", "--cursor", "--copilot", "--codex", "--project", "--yes");
        Assert.Equal(0, add.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "agents", "code-reviewer.md")));
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".cursor", "agents", "code-reviewer.md")));
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".github", "agents", "code-reviewer.agent.md")));

        var codexToml = File.ReadAllText(Path.Combine(WorkDir(temp), ".codex", "agents", "code-reviewer.toml"));
        Assert.Contains("name = \"code-reviewer\"", codexToml);
        Assert.Contains("developer_instructions = \"\"\"", codexToml);
    }

    [Fact]
    public void AddRuleInstallsCursorMdcAndClaudeTranslation()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var dir = Path.Combine(WorkDir(temp), "assets", "rules", "ts-style");
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "content", "ts-style.mdc"),
            "---\ndescription: TS rules.\nglobs: \"*.ts\"\n---\n\nPrefer explicit return types.\n");
        File.WriteAllText(Path.Combine(dir, "agentpack.yaml"),
            "name: TS Style\nversion: 1.0.0\ndescription: Test rule.\ngroups: [review]\n");

        var add = RunCli(temp, "add", "ts-style", "--claude", "--cursor", "--project", "--yes");
        Assert.Equal(0, add.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".cursor", "rules", "ts-style.mdc")));

        var claudeRule = File.ReadAllText(Path.Combine(WorkDir(temp), ".claude", "rules", "ts-style.md"));
        Assert.Contains("paths:", claudeRule);
        Assert.Contains("\"*.ts\"", claudeRule);
        Assert.DoesNotContain("globs", claudeRule);

        // Re-adding is a no-op, and remove deletes both provider files.
        var again = RunCli(temp, "add", "ts-style", "--claude", "--cursor", "--project", "--yes");
        Assert.Equal(0, again.ExitCode);
        var remove = RunCli(temp, "remove", "ts-style", "--project", "--yes");
        Assert.Equal(0, remove.ExitCode);
        Assert.False(File.Exists(Path.Combine(WorkDir(temp), ".claude", "rules", "ts-style.md")));
        Assert.False(File.Exists(Path.Combine(WorkDir(temp), ".cursor", "rules", "ts-style.mdc")));
    }

    [Fact]
    public void AddUnknownAssetSuggestsNearest()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "add", "demo-skil", "--claude", "--project", "--yes");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("demo-skill", result.Output + result.Error);
    }

    [Fact]
    public void BlockedAssetCannotBeInstalledExplicitly()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "banned", "status: blocked");

        var result = RunCli(temp, "add", "banned", "--claude", "--project", "--yes");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("blocked", (result.Output + result.Error).ToLowerInvariant());
    }

    [Fact]
    public void AddWithoutTerminalAndWithoutArgsGivesGuidance()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "add", "--claude", "--project");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("agentpack add grill-me", result.Output + result.Error);
    }

    [Fact]
    public void AddEverythingWithYesIsRefused()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var result = RunCli(temp, "add", "--claude", "--project", "--yes");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("entire catalog", result.Output + result.Error);
    }

    [Fact]
    public void AddByKindInstallsAllOfKindWhenNonInteractive()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "skill-one");
        WriteSkill(temp, "skill-two");

        var result = RunCli(temp, "add", "skills", "--claude", "--project", "--yes");
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "skill-one", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(WorkDir(temp), ".claude", "skills", "skill-two", "SKILL.md")));
    }

    [Fact]
    public void ListHidesStatusAndSourceColumnsWhenAllDefault()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");

        var plain = RunCli(temp, "list");
        Assert.Equal(0, plain.ExitCode);
        Assert.Contains("demo-skill", plain.Output);
        Assert.DoesNotContain("Status", plain.Output);
        Assert.DoesNotContain("Source", plain.Output);

        // A non-default status brings the column back.
        WriteSkill(temp, "old-skill", "status: deprecated");
        var withStatus = RunCli(temp, "list");
        Assert.Equal(0, withStatus.ExitCode);
        Assert.Contains("Status", withStatus.Output);
        Assert.Contains("deprecated", withStatus.Output);
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
        Assert.Contains("agentpack add", result.Output);
    }

    [Fact]
    public void PlanCollapsesUpToDateRows()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var ids = new[] { "skill-aa", "skill-bb", "skill-cc", "skill-dd", "skill-ee", "skill-ff", "skill-gg" };
        foreach (var id in ids) WriteSkill(temp, id);

        Assert.Equal(0, RunCli(temp, "add", "skills", "--claude", "--project", "--yes").ExitCode);

        var plan = RunCli(temp, "plan", "skills", "--claude", "--project");
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
        Assert.Equal(0, RunCli(temp, "add", "skills", "--claude", "--project", "--yes").ExitCode);

        // One new asset makes the second add actionable; the 7 untouched installs collapse.
        WriteSkill(temp, "skill-new");
        var again = RunCli(temp, "add", "skills", "--claude", "--project", "--yes");
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

    private static (int ExitCode, string Output, string Error) RunCli(TempDir temp, params string[] args)
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

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "CLI process timed out");
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
