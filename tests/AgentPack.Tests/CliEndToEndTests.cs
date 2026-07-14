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
        Assert.Contains("name: \"Grill Me\"", yaml);
        Assert.DoesNotContain("checksum", yaml);

        // A second run without --force refuses to overwrite.
        var again = RunCli(temp, "new", "skills", "grill-me");
        Assert.NotEqual(0, again.ExitCode);
        Assert.Contains("--force", again.Output + again.Error);
    }

    [Fact]
    public void NewAgentScaffoldsTypedDependencies()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var result = RunCli(temp, "new", "agents", "upgrade",
            "--tool", "read", "--tool", "execute",
            "--instruction", "conventions", "--skill", "analysis", "--mcp", "docs");
        Assert.Equal(0, result.ExitCode);
        var root = Path.Combine(WorkDir(temp), "assets", "agents", "upgrade");
        Assert.True(File.Exists(Path.Combine(root, "content", "AGENT.md")));
        var manifest = File.ReadAllText(Path.Combine(root, "agentpack.yaml"));
        Assert.Contains("tools: [read, execute]", manifest);
        Assert.DoesNotContain("model", manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("instructions: [conventions]", manifest);
        Assert.Contains("skills: [analysis]", manifest);
        Assert.Contains("mcp: [docs]", manifest);
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
        Assert.Contains("source: https://github.com/example/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d", manifest);
    }

    [Fact]
    public void ImportScaffoldsReviewedExternalHookMetadata()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var result = RunCli(temp, "import",
            "https://github.com/example/hooks/tree/main/guard@9d2f1ae187231d8199c64b5b762e1bdf2244733d",
            "--kind", "hooks", "--id", "guard",
            "--hook-trigger", "postToolUse", "--hook-tool", "Edit",
            "--hook-command", "guard.sh", "--hook-timeout", "12");

        Assert.Equal(0, result.ExitCode);
        var manifest = File.ReadAllText(Path.Combine(WorkDir(temp), "assets", "hooks", "guard", "agentpack.yaml"));
        Assert.Contains("trigger: postToolUse", manifest);
        Assert.Contains("tool: \"Edit\"", manifest);
        Assert.Contains("command: \"guard.sh\"", manifest);
        Assert.Contains("timeoutSec: 12", manifest);
    }

    [Fact]
    public void ImportScaffoldsTypedExternalMcpMetadataAndToolInventory()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        var result = RunCli(temp, "import",
            "https://github.com/example/mcp/tree/main/docs@9d2f1ae187231d8199c64b5b762e1bdf2244733d",
            "--kind", "mcp", "--id", "docs",
            "--mcp-server", "docs", "--mcp-transport", "http",
            "--mcp-url", "https://example.test/mcp",
            "--mcp-tool", "search",
            "--mcp-header-env", "Authorization=DOCS_TOKEN");

        Assert.Equal(0, result.ExitCode);
        var manifest = File.ReadAllText(Path.Combine(WorkDir(temp), "assets", "mcp", "docs", "agentpack.yaml"));
        Assert.Contains("transport: http", manifest);
        Assert.Contains("url: \"https://example.test/mcp\"", manifest);
        Assert.Contains("tools: [\"search\"]", manifest);
        Assert.Contains("\"Authorization\": \"DOCS_TOKEN\"", manifest);
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("templates")]
    public void ImportRejectsKindsWithoutExternalInstallationContracts(string kind)
    {
        using var temp = new TempDir();
        WriteCatalog(temp);

        var result = RunCli(temp, "import",
            "https://github.com/example/repo@9d2f1ae187231d8199c64b5b762e1bdf2244733d",
            "--kind", kind, "--id", "demo");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not supported", (result.Output + result.Error).ToLowerInvariant());
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
    public void AddShowsDeclaredLicenseForAutomaticExternalDependencyButPlanDoesNot()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteExternalSkill(temp, "licensed-dependency", "MIT");
        WriteAgent(temp, "reviewer");
        var agentManifest = Path.Combine(WorkDir(temp), "assets", "agents", "reviewer", "agentpack.yaml");
        File.WriteAllText(agentManifest, File.ReadAllText(agentManifest).Replace(
            "imports: {}",
            "imports:\n    skills: [licensed-dependency]"));

        var plan = RunCli(temp, "plan", "reviewer", "--claude", "--project");
        Assert.Equal(0, plan.ExitCode);
        Assert.DoesNotContain("Third-party license notice", plan.Output + plan.Error);

        var add = RunCli(temp, "add", "reviewer", "--claude", "--project", "--yes");
        var output = add.Output + add.Error;
        Assert.Equal(0, add.ExitCode);
        Assert.Contains("Third-party license notice", output);
        Assert.Contains("licensed-dependency", output);
        Assert.Contains("MIT", output);
    }

    [Fact]
    public void AddWarnsWhenExternalAssetHasNoRecordedLicense()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteExternalSkill(temp, "unlicensed-dependency", null);

        var add = RunCli(temp, "add", "unlicensed-dependency", "--claude", "--project", "--yes");
        var output = add.Output + add.Error;
        Assert.Equal(0, add.ExitCode);
        Assert.Contains("not recorded", output);
        Assert.Contains("No license is recorded for external asset 'unlicensed-dependency'", output);
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
    public void YesDoesNotChooseDriftPolicy()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteSkill(temp, "demo-skill");
        Assert.Equal(0, RunCli(temp, "add", "demo-skill", "--claude", "--project", "--yes").ExitCode);
        var installed = Path.Combine(WorkDir(temp), ".claude", "skills", "demo-skill", "SKILL.md");
        File.WriteAllText(installed, "local\n");

        var undecided = RunCli(temp, "add", "demo-skill", "--claude", "--project", "--yes");
        Assert.Equal(3, undecided.ExitCode);
        Assert.Contains("--force", undecided.Output + undecided.Error);

        Assert.Equal(0, RunCli(temp, "add", "demo-skill", "--claude", "--project", "--yes", "--keep-local").ExitCode);
        Assert.Equal("local\n", File.ReadAllText(installed));
        Assert.Equal(0, RunCli(temp, "add", "demo-skill", "--claude", "--project", "--yes", "--force").ExitCode);
        Assert.Equal("# demo-skill\n", File.ReadAllText(installed));
    }

    [Fact]
    public void CatalogCompileChecksNativeAgentSamples()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteAgent(temp, "upgrade");
        Assert.Equal(0, RunCli(temp, "catalog", "lock", "--no-fetch").ExitCode);

        var compiled = RunCli(temp, "catalog", "compile");
        Assert.Equal(0, compiled.ExitCode);
        Assert.Contains("8 native agent output", compiled.Output + compiled.Error);
    }

    [Fact]
    public void AgentExplainShowsExactCoarseAndModelBehavior()
    {
        using var temp = new TempDir();
        WriteCatalog(temp);
        WriteAgent(temp, "governance");
        var manifest = Path.Combine(WorkDir(temp), "assets", "agents", "governance", "agentpack.yaml");
        File.WriteAllText(manifest, """
            name: Governance Reviewer
            version: 1.0.0
            description: Reviews agent governance controls.
            groups: [review]
            providers: [claude, codex, copilot, cursor]
            agent:
              tools: [read, search, execute]
              models:
                copilot: gpt-4o
              imports: {}
            """);

        var result = RunCli(temp, "agent", "explain", "governance",
            "--claude", "--codex", "--copilot", "--cursor");

        Assert.Equal(0, result.ExitCode);
        var output = result.Output + result.Error;
        Assert.Contains("Agent provider compatibility", output);
        Assert.Contains("exact", output);
        Assert.Contains("coarse", output);
        Assert.Contains("current/default", output);
        Assert.Contains("writable", output);
        Assert.Contains("tools: [read, search]", output);
        Assert.Contains("always strips model metadata", output);
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

    private static void WriteSkill(TempDir temp, string id, string? extraYaml = null)
    {
        var dir = Path.Combine(WorkDir(temp), "assets", "skills", id);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "content", "SKILL.md"), $"# {id}\n");
        File.WriteAllText(Path.Combine(dir, "agentpack.yaml"),
            $"name: {id}\nversion: 1.0.0\ndescription: Test skill.\ngroups: [review]\n{extraYaml}\n");
    }

    private static void WriteAgent(TempDir temp, string id)
    {
        var dir = Path.Combine(WorkDir(temp), "assets", "agents", id);
        Directory.CreateDirectory(Path.Combine(dir, "content"));
        File.WriteAllText(Path.Combine(dir, "content", "AGENT.md"), $"# {id}\n\nDo the work.\n");
        File.WriteAllText(Path.Combine(dir, "agentpack.yaml"),
            $"name: {id}\nversion: 1.0.0\ndescription: Test agent.\ngroups: [review]\nagent:\n  tools: [read, search]\n  imports: {{}}\n");
    }

    private static (string Repo, string Revision) WriteExternalSkill(TempDir temp, string id, string? license)
    {
        var repo = Path.Combine(temp.Path, $"{id}.git");
        Directory.CreateDirectory(repo);
        RunGit(repo, "init", "--quiet");
        RunGit(repo, "config", "user.email", "agentpack-tests@example.invalid");
        RunGit(repo, "config", "user.name", "AgentPack Tests");
        File.WriteAllText(Path.Combine(repo, "SKILL.md"), $"# {id}\n");
        RunGit(repo, "add", "SKILL.md");
        RunGit(repo, "commit", "--quiet", "-m", "Add test skill");
        var revision = RunGit(repo, "rev-parse", "HEAD").Trim();

        var dir = Path.Combine(WorkDir(temp), "assets", "skills", id);
        Directory.CreateDirectory(dir);
        var licenseLine = license is null ? "" : $"  license: {license}\n";
        File.WriteAllText(Path.Combine(dir, "agentpack.yaml"),
            $"name: {id}\nversion: 1.0.0\ndescription: External test skill.\ngroups: [review]\n" +
            $"source:\n  url: \"{repo}\"\n  ref: {revision}\n{licenseLine}");
        return (repo, revision);
    }

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
        return output;
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
