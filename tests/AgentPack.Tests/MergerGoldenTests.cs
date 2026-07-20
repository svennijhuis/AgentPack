using AgentPack.Core;

namespace AgentPack.Tests;

/// <summary>
/// Golden-file tests for provider config merges: exact output per provider format.
/// These are the tests that catch provider format drift.
/// </summary>
public class MergerGoldenTests
{
    private static McpServer GitHubMcp => new()
    {
        Server = "github",
        Command = "github-mcp-server",
        EnvVars = ["GITHUB_TOKEN"]
    };

    [Fact]
    public void ClaudeProjectMcpGolden()
    {
        var output = MergeMcp(ProviderName.Claude, InstallScope.Project, ".mcp.json");
        Assert.Equal(Normalize("""
            {
              "mcpServers": {
                "github": {
                  "type": "stdio",
                  "command": "github-mcp-server",
                  "args": [],
                  "env": {
                    "GITHUB_TOKEN": "${GITHUB_TOKEN}"
                  }
                }
              }
            }
            """), Normalize(output));
    }

    [Fact]
    public void CopilotProjectMcpGolden()
    {
        // .vscode/mcp.json: root key "servers", env vars via VS Code's ${env:VAR} syntax.
        var output = MergeMcp(ProviderName.Copilot, InstallScope.Project, Path.Combine(".vscode", "mcp.json"));
        Assert.Contains("\"servers\"", output);
        Assert.DoesNotContain("\"mcpServers\"", output);
        Assert.Contains("\"GITHUB_TOKEN\": \"${env:GITHUB_TOKEN}\"", output);
    }

    [Fact]
    public void CopilotUserMcpGolden()
    {
        // Copilot CLI documents no placeholder expansion: env object omitted
        // (stdio servers inherit the shell env) and tools allowlist is explicit.
        var output = MergeMcp(ProviderName.Copilot, InstallScope.User, Path.Combine(".copilot", "mcp-config.json"));
        Assert.Equal(Normalize("""
            {
              "mcpServers": {
                "github": {
                  "type": "stdio",
                  "command": "github-mcp-server",
                  "args": [],
                  "tools": [
                    "*"
                  ]
                }
              }
            }
            """), Normalize(output));
    }

    [Fact]
    public void CursorMcpUsesEnvColonPlaceholders()
    {
        var output = MergeMcp(ProviderName.Cursor, InstallScope.Project, Path.Combine(".cursor", "mcp.json"));
        Assert.Contains("\"mcpServers\"", output);
        Assert.Contains("\"GITHUB_TOKEN\": \"${env:GITHUB_TOKEN}\"", output);
    }

    [Fact]
    public void CodexHttpMcpUsesEnvHttpHeaders()
    {
        using var temp = new TempDir();
        var asset = TestData.Asset(AssetKind.Mcp, "remote-api", mcp: new McpServer
        {
            Server = "remote-api",
            Transport = McpTransport.Http,
            Url = "https://mcp.example.com/mcp",
            HeaderEnvVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "API_TOKEN" }
        });
        var targetPath = Path.Combine(temp.Path, ".codex", "config.toml");
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Mcp, Path.Combine(".codex", "config.toml"), InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        var output = File.ReadAllText(targetPath);
        Assert.Contains("env_http_headers = { Authorization = \"API_TOKEN\" }", output);
        Assert.DoesNotContain("${", output);
    }

    [Fact]
    public void CodexMcpTomlGolden()
    {
        var output = MergeMcp(ProviderName.Codex, InstallScope.Project, Path.Combine(".codex", "config.toml"));
        Assert.Equal(Normalize("""
            [mcp_servers.github]
            command = "github-mcp-server"
            env_vars = ["GITHUB_TOKEN"]
            """), Normalize(output));
    }

    [Fact]
    public void CodexTomlQuotedServerIdRoundTrip()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, "model = \"o4\"\n\n[mcp_servers.other]\ncommand = \"other-server\"\n");

        // A dotted server id is not a bare TOML key and must be quoted in the header.
        var asset = TestData.Asset(AssetKind.Mcp, "dotted", mcp: new McpServer
        {
            Server = "my.server",
            Command = "run-server",
            EnvVars = ["TOKEN"]
        });
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Mcp, Path.Combine(".codex", "config.toml"), InstallMode.MergeMcp);
        var result = McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        var merged = File.ReadAllText(targetPath);
        Assert.Contains("[mcp_servers.\"my.server\"]", merged);
        Assert.Equal(FragmentState.Present, McpMerger.CheckFragment(targetPath, ProviderName.Codex, InstallScope.Project, result.Fragment));

        McpMerger.RemoveFragment(targetPath, ProviderName.Codex, InstallScope.Project, result.Fragment, _ => { });
        var removed = File.ReadAllText(targetPath);
        Assert.DoesNotContain("my.server", removed);
        Assert.Contains("model = \"o4\"", removed);
        Assert.Contains("[mcp_servers.other]", removed);
    }

    [Fact]
    public void CodexTomlEscapesQuotesAndBackslashes()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".codex", "config.toml");
        var asset = TestData.Asset(AssetKind.Mcp, "windowsy", mcp: new McpServer
        {
            Server = "windowsy",
            Command = "C:\\tools\\run \"it\".exe"
        });
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Mcp, Path.Combine(".codex", "config.toml"), InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        Assert.Contains("command = \"C:\\\\tools\\\\run \\\"it\\\".exe\"", File.ReadAllText(targetPath));
    }

    [Fact]
    public void CodexTomlEscapesControlCharacters()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".codex", "config.toml");
        var asset = TestData.Asset(AssetKind.Mcp, "multiline", mcp: new McpServer
        {
            Server = "multiline",
            Command = "run",
            Args = ["line1\nline2\ttabbed"]
        });
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Mcp, Path.Combine(".codex", "config.toml"), InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        var output = File.ReadAllText(targetPath);
        // A raw newline inside a TOML basic string is invalid; it must be escaped.
        Assert.Contains("args = [\"line1\\nline2\\ttabbed\"]", output);
    }

    [Fact]
    public void CodexTomlStdioWithArgsAndSortedEnvVars()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".codex", "config.toml");
        var asset = TestData.Asset(AssetKind.Mcp, "argsy", mcp: new McpServer
        {
            Server = "argsy",
            Command = "run-server",
            Args = ["--port", "8080"],
            EnvVars = ["B_TOKEN", "A_TOKEN"]
        });
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Mcp, Path.Combine(".codex", "config.toml"), InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        var output = File.ReadAllText(targetPath);
        Assert.Contains("args = [\"--port\", \"8080\"]", output);
        Assert.Contains("env_vars = [\"A_TOKEN\", \"B_TOKEN\"]", output);
    }

    [Fact]
    public void CopilotUserRemoteHeadersAreRejected()
    {
        using var temp = new TempDir();
        // Copilot CLI does not expand env refs; a literal ${VAR} would be sent
        // to the server as the header value, so this must fail with a hint.
        var asset = TestData.Asset(AssetKind.Mcp, "remote-api", mcp: new McpServer
        {
            Server = "remote-api",
            Transport = McpTransport.Http,
            Url = "https://mcp.example.com/mcp",
            HeaderEnvVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "API_TOKEN" }
        });
        var targetPath = Path.Combine(temp.Path, ".copilot", "mcp-config.json");
        var target = new InstallTarget(ProviderName.Copilot, AssetKind.Mcp, Path.Combine(".copilot", "mcp-config.json"), InstallMode.MergeMcp);

        var ex = Assert.Throws<AgentPackException>(() =>
            McpMerger.Apply(asset, null, target, targetPath, InstallScope.User, _ => { }));
        Assert.Contains("project scope", ex.Hint);
    }

    [Theory]
    [InlineData(ProviderName.Claude, ".mcp.json", "\"Authorization\": \"${API_TOKEN}\"")]
    [InlineData(ProviderName.Cursor, ".cursor/mcp.json", "\"Authorization\": \"${env:API_TOKEN}\"")]
    public void RemoteHeadersUseProviderPlaceholderSyntax(ProviderName provider, string relativeTarget, string expectedHeader)
    {
        using var temp = new TempDir();
        var asset = TestData.Asset(AssetKind.Mcp, "remote-api", mcp: new McpServer
        {
            Server = "remote-api",
            Transport = McpTransport.Http,
            Url = "https://mcp.example.com/mcp",
            HeaderEnvVars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = "API_TOKEN" }
        });
        var targetPath = Path.Combine(temp.Path, relativeTarget.Replace('/', Path.DirectorySeparatorChar));
        var target = new InstallTarget(provider, AssetKind.Mcp, relativeTarget, InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        var output = File.ReadAllText(targetPath);
        Assert.Contains("\"url\": \"https://mcp.example.com/mcp\"", output);
        Assert.Contains(expectedHeader, output);
    }

    [Fact]
    public void StdioServerWithoutEnvVarsWritesEmptyEnvObject()
    {
        using var temp = new TempDir();
        var asset = TestData.Asset(AssetKind.Mcp, "plain", mcp: new McpServer { Server = "plain", Command = "run" });
        var targetPath = Path.Combine(temp.Path, ".mcp.json");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Mcp, ".mcp.json", InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        Assert.Contains("\"env\": {}", File.ReadAllText(targetPath));
    }

    [Fact]
    public void CursorHookMergeKeepsExistingVersionField()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".cursor", "hooks.json");
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, "{ \"version\": 2, \"hooks\": {} }");

        var config = ApplyHook(temp, ProviderName.Cursor, new HookSpec { Trigger = HookTrigger.PreToolUse, Command = "hook.sh" });

        Assert.Contains("\"version\": 2", config);
    }

    [Fact]
    public void McpMergePreservesUnrelatedUserEntries()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".mcp.json");
        File.WriteAllText(targetPath, """
            {
              "mcpServers": {
                "mine": { "type": "stdio", "command": "my-server" }
              },
              "otherSetting": true
            }
            """);

        var asset = TestData.Asset(AssetKind.Mcp, "github", mcp: GitHubMcp);
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Mcp, ".mcp.json", InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { });

        var output = File.ReadAllText(targetPath);
        Assert.Contains("\"mine\"", output);
        Assert.Contains("\"otherSetting\"", output);
        Assert.Contains("\"github\"", output);
    }

    [Fact]
    public void McpMergeRejectsConflictingExistingServer()
    {
        using var temp = new TempDir();
        var targetPath = Path.Combine(temp.Path, ".mcp.json");
        File.WriteAllText(targetPath, """
            { "mcpServers": { "github": { "type": "stdio", "command": "different-server" } } }
            """);

        var asset = TestData.Asset(AssetKind.Mcp, "github", mcp: GitHubMcp);
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Mcp, ".mcp.json", InstallMode.MergeMcp);

        var ex = Assert.Throws<AgentPackException>(() =>
            McpMerger.Apply(asset, null, target, targetPath, InstallScope.Project, _ => { }));
        Assert.Equal(ExitCodes.DriftOrConflict, ex.ExitCode);
    }

    [Fact]
    public void ClaudeHookGolden()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "secret-scan",
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Tool = "Bash", Command = "hook.sh", TimeoutSec = 30 });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".claude", "settings.json");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Hooks, Path.Combine(".claude", "settings.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        Assert.Equal(Normalize("""
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      {
                        "type": "command",
                        "command": "./.claude/hooks/secret-scan/hook.sh",
                        "timeout": 30
                      }
                    ]
                  }
                ]
              }
            }
            """), Normalize(File.ReadAllText(targetPath)));

        var installedHook = Path.Combine(temp.Path, ".claude", "hooks", "secret-scan", "hook.sh");
        Assert.True(File.Exists(installedHook));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(File.GetUnixFileMode(installedHook).HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void CursorHookGolden()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "secret-scan",
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Command = "hook.sh", TimeoutSec = 30 });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".cursor", "hooks.json");
        var target = new InstallTarget(ProviderName.Cursor, AssetKind.Hooks, Path.Combine(".cursor", "hooks.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        Assert.Equal(Normalize("""
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  {
                    "command": "./.cursor/hooks/secret-scan/hook.sh",
                    "timeout": 30
                  }
                ]
              }
            }
            """), Normalize(File.ReadAllText(targetPath)));
    }

    [Fact]
    public void CodexHookGolden()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "secret-scan",
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Tool = "Bash", Command = "hook.sh", TimeoutSec = 30 });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".codex", "hooks.json");
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Hooks, Path.Combine(".codex", "hooks.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        Assert.Equal(Normalize("""
            {
              "hooks": {
                "PreToolUse": [
                  {
                    "matcher": "Bash",
                    "hooks": [
                      {
                        "type": "command",
                        "command": "./.codex/hooks/secret-scan/hook.sh",
                        "timeout": 30
                      }
                    ]
                  }
                ]
              }
            }
            """), Normalize(File.ReadAllText(targetPath)));
    }

    [Theory]
    // The event vocabulary diverges per provider: Claude/Codex PascalCase,
    // Cursor and Copilot camelCase with different renames for the same trigger.
    [InlineData(ProviderName.Claude, HookTrigger.Stop, "Stop")]
    [InlineData(ProviderName.Claude, HookTrigger.SessionStart, "SessionStart")]
    [InlineData(ProviderName.Claude, HookTrigger.UserPromptSubmit, "UserPromptSubmit")]
    [InlineData(ProviderName.Claude, HookTrigger.Notification, "Notification")]
    [InlineData(ProviderName.Codex, HookTrigger.Stop, "Stop")]
    [InlineData(ProviderName.Codex, HookTrigger.SessionStart, "SessionStart")]
    [InlineData(ProviderName.Codex, HookTrigger.UserPromptSubmit, "UserPromptSubmit")]
    [InlineData(ProviderName.Cursor, HookTrigger.PostToolUse, "postToolUse")]
    [InlineData(ProviderName.Cursor, HookTrigger.Stop, "stop")]
    [InlineData(ProviderName.Cursor, HookTrigger.SessionStart, "sessionStart")]
    [InlineData(ProviderName.Cursor, HookTrigger.UserPromptSubmit, "beforeSubmitPrompt")]
    [InlineData(ProviderName.Copilot, HookTrigger.PostToolUse, "postToolUse")]
    [InlineData(ProviderName.Copilot, HookTrigger.Stop, "agentStop")]
    [InlineData(ProviderName.Copilot, HookTrigger.SessionStart, "sessionStart")]
    [InlineData(ProviderName.Copilot, HookTrigger.UserPromptSubmit, "userPromptSubmitted")]
    [InlineData(ProviderName.Copilot, HookTrigger.Notification, "notification")]
    public void HookTriggerVocabularyPerProvider(ProviderName provider, HookTrigger trigger, string expectedEvent)
    {
        using var temp = new TempDir();
        var config = ApplyHook(temp, provider, new HookSpec { Trigger = trigger, Command = "hook.sh" });

        Assert.Contains($"\"{expectedEvent}\"", config);
        if (trigger != HookTrigger.PostToolUse)
        {
            // Non-tool events must not get a defaulted tool matcher — it would
            // keep the hook from ever firing (Claude matches SessionStart on
            // session source, not tool names).
            Assert.DoesNotContain("matcher", config);
        }
    }

    [Theory]
    [InlineData(ProviderName.Codex)]
    [InlineData(ProviderName.Cursor)]
    public void NotificationTriggerIsRejectedWhereUnsupported(ProviderName provider)
    {
        using var temp = new TempDir();
        var ex = Assert.Throws<AgentPackException>(() =>
            ApplyHook(temp, provider, new HookSpec { Trigger = HookTrigger.Notification, Command = "hook.sh" }));
        Assert.Contains("notification", ex.Message);
    }

    [Fact]
    public void ExplicitMatcherOnNonToolEventIsHonored()
    {
        using var temp = new TempDir();
        var config = ApplyHook(temp, ProviderName.Claude,
            new HookSpec { Trigger = HookTrigger.SessionStart, Tool = "startup", Command = "hook.sh" });

        Assert.Contains("\"matcher\": \"startup\"", config);
    }

    [Fact]
    public void SessionStartHookGoldenHasNoMatcher()
    {
        using var temp = new TempDir();
        var config = ApplyHook(temp, ProviderName.Claude,
            new HookSpec { Trigger = HookTrigger.SessionStart, Command = "hook.sh", TimeoutSec = 30 });

        Assert.Equal(Normalize("""
            {
              "hooks": {
                "SessionStart": [
                  {
                    "hooks": [
                      {
                        "type": "command",
                        "command": "./.claude/hooks/notify/hook.sh",
                        "timeout": 30
                      }
                    ]
                  }
                ]
              }
            }
            """), Normalize(config));
    }

    private static string ApplyHook(TempDir temp, ProviderName provider, HookSpec hook)
    {
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "notify", hook: hook);
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "notify", "content");
        var relative = provider switch
        {
            ProviderName.Claude => Path.Combine(".claude", "settings.json"),
            ProviderName.Codex => Path.Combine(".codex", "hooks.json"),
            ProviderName.Cursor => Path.Combine(".cursor", "hooks.json"),
            _ => Path.Combine(".github", "hooks", "notify.json")
        };
        var targetPath = Path.Combine(temp.Path, relative);
        var target = new InstallTarget(provider, AssetKind.Hooks, relative, InstallMode.MergeHook);
        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });
        return File.ReadAllText(targetPath);
    }

    [Fact]
    public void CopilotHookGolden()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "secret-scan",
            hook: new HookSpec { Trigger = HookTrigger.PreToolUse, Command = "hook.sh", TimeoutSec = 30 });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".github", "hooks", "secret-scan.json");
        var target = new InstallTarget(ProviderName.Copilot, AssetKind.Hooks, Path.Combine(".github", "hooks", "secret-scan.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        Assert.Equal(Normalize("""
            {
              "version": 1,
              "hooks": {
                "preToolUse": [
                  {
                    "type": "command",
                    "bash": "./.github/hooks/secret-scan/hook.sh",
                    "timeoutSec": 30
                  }
                ]
              }
            }
            """), Normalize(File.ReadAllText(targetPath)));

        Assert.True(File.Exists(Path.Combine(temp.Path, ".github", "hooks", "secret-scan", "hook.sh")));
    }

    [Fact]
    public void CopilotHookIncludesPowershellTwinWhenPresent()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "cross-os",
            files: new Dictionary<string, string>
            {
                ["hook.sh"] = "#!/usr/bin/env bash\necho ok\n",
                ["hook.ps1"] = "Write-Output 'ok'\n"
            },
            hook: new HookSpec { Command = "hook.sh" });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "cross-os", "content");
        var targetPath = Path.Combine(temp.Path, ".github", "hooks", "cross-os.json");
        var target = new InstallTarget(ProviderName.Copilot, AssetKind.Hooks, Path.Combine(".github", "hooks", "cross-os.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        var output = File.ReadAllText(targetPath);
        Assert.Contains("\"bash\": \"./.github/hooks/cross-os/hook.sh\"", output);
        Assert.Contains("\"powershell\": \"./.github/hooks/cross-os/hook.ps1\"", output);
    }

    [Fact]
    public void HookMergeIsIdempotent()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "secret-scan", hook: new HookSpec { Command = "hook.sh" });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "secret-scan", "content");
        var targetPath = Path.Combine(temp.Path, ".claude", "settings.json");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Hooks, Path.Combine(".claude", "settings.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });
        var first = File.ReadAllText(targetPath);
        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        Assert.Equal(first, File.ReadAllText(targetPath));
    }

    [Fact]
    public void CopilotPowershellTwinInSubfolderUsesItsRealPath()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Hooks, "ps-twin",
            files: new Dictionary<string, string>
            {
                ["hook.sh"] = "#!/usr/bin/env bash\necho ok\n",
                ["win/hook.ps1"] = "Write-Output ok\n"
            },
            hook: new HookSpec { Command = "hook.sh" });
        var sourcePath = Path.Combine(temp.Path, "assets", "hooks", "ps-twin", "content");
        var targetPath = Path.Combine(temp.Path, ".github", "hooks", "ps-twin.json");
        var target = new InstallTarget(ProviderName.Copilot, AssetKind.Hooks, Path.Combine(".github", "hooks", "ps-twin.json"), InstallMode.MergeHook);

        HookMerger.Apply(asset, sourcePath, target, targetPath, temp.Path, InstallScope.Project, _ => { });

        // The registered path must be where the twin actually landed, not a
        // same-directory guess next to hook.sh.
        Assert.Contains("\"powershell\": \"./.github/hooks/ps-twin/win/hook.ps1\"", File.ReadAllText(targetPath));
        Assert.True(File.Exists(Path.Combine(temp.Path, ".github", "hooks", "ps-twin", "win", "hook.ps1")));
    }

    [Fact]
    public void McpSecretsInRawContentAreRejected()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Mcp, "leaky",
            files: new Dictionary<string, string>
            {
                ["mcp.json"] = """{"name":"leaky","transport":"stdio","command":"x","env":{"TOKEN":"actual-secret-value"}}"""
            });
        var sourcePath = Path.Combine(temp.Path, "assets", "mcp", "leaky", "content");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Mcp, ".mcp.json", InstallMode.MergeMcp);

        var ex = Assert.Throws<AgentPackException>(() =>
            McpMerger.Apply(asset, sourcePath, target, Path.Combine(temp.Path, ".mcp.json"), InstallScope.Project, _ => { }));
        Assert.Contains("must be declared by name", ex.Message);
    }

    [Fact]
    public void McpSecretsInRawMcpServersPassthroughAreRejected()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Mcp, "leaky",
            files: new Dictionary<string, string>
            {
                ["mcp.json"] = """{"mcpServers":{"leaky":{"command":"x","env":{"TOKEN":"actual-secret-value"}}}}"""
            });
        var sourcePath = Path.Combine(temp.Path, "assets", "mcp", "leaky", "content");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Mcp, ".mcp.json", InstallMode.MergeMcp);

        var ex = Assert.Throws<AgentPackException>(() =>
            McpMerger.Apply(asset, sourcePath, target, Path.Combine(temp.Path, ".mcp.json"), InstallScope.Project, _ => { }));
        Assert.Contains("must be declared by name", ex.Message);
    }

    [Fact]
    public void McpRawMcpServersPassthroughRewritesDeclaredEnvVars()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Mcp, "multi",
            files: new Dictionary<string, string>
            {
                ["mcp.json"] = """{"mcpServers":{"multi":{"command":"x","env":{"TOKEN":"${TOKEN}"}}}}"""
            });
        var sourcePath = Path.Combine(temp.Path, "assets", "mcp", "multi", "content");
        var targetPath = Path.Combine(temp.Path, ".cursor", "mcp.json");
        var target = new InstallTarget(ProviderName.Cursor, AssetKind.Mcp, Path.Combine(".cursor", "mcp.json"), InstallMode.MergeMcp);

        McpMerger.Apply(asset, sourcePath, target, targetPath, InstallScope.Project, _ => { });

        Assert.Contains("${env:TOKEN}", File.ReadAllText(targetPath));
    }

    [Fact]
    public void ClaudeRuleGoldenTranslatesGlobsToPaths()
    {
        var asset = TestData.Asset(AssetKind.Rules, "ts-style");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Rules, Path.Combine(".claude", "rules", "ts-style.md"), InstallMode.ConvertFile, IsFileTarget: true);
        var output = FileConverter.Convert(asset, target, """
            ---
            description: TypeScript style rules.
            globs: "*.ts,src/**/*.tsx"
            alwaysApply: false
            ---

            # TypeScript style

            Prefer explicit return types.
            """);

        Assert.Equal(Normalize("""
            ---
            description: TypeScript style rules.
            paths:
              - "*.ts"
              - src/**/*.tsx
            ---

            # TypeScript style

            Prefer explicit return types.
            """), Normalize(output));
    }

    [Fact]
    public void ClaudeRuleGoldenAlwaysApplyDropsPathScoping()
    {
        // alwaysApply: true means "load unconditionally" — for Claude that is a
        // rule without paths frontmatter, never a rule scoped to the old globs.
        var asset = TestData.Asset(AssetKind.Rules, "commit-style");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Rules, Path.Combine(".claude", "rules", "commit-style.md"), InstallMode.ConvertFile, IsFileTarget: true);
        var output = FileConverter.Convert(asset, target, """
            ---
            description: Commit message rules.
            globs: ["*.md"]
            alwaysApply: true
            ---

            Use imperative commit subjects.
            """);

        Assert.Equal(Normalize("""
            ---
            description: Commit message rules.
            ---

            Use imperative commit subjects.
            """), Normalize(output));
        Assert.DoesNotContain("paths:", output);
        Assert.DoesNotContain("alwaysApply", output);
    }

    [Fact]
    public void ClaudeRuleGoldenWithoutFrontmatterIsBodyOnly()
    {
        var asset = TestData.Asset(AssetKind.Rules, "plain");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Rules, Path.Combine(".claude", "rules", "plain.md"), InstallMode.ConvertFile, IsFileTarget: true);
        var output = FileConverter.Convert(asset, target, "Always run the linter before committing.\n");

        Assert.Equal("Always run the linter before committing.\n", output);
    }

    [Fact]
    public void CodexAgentGoldenTranslatesMarkdownToToml()
    {
        var asset = TestData.Asset(AssetKind.Agents, "code-reviewer");
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Agents, Path.Combine(".codex", "agents", "code-reviewer.toml"), InstallMode.ConvertFile, IsFileTarget: true);
        var output = FileConverter.Convert(asset, target, """
            ---
            name: code-reviewer
            description: Reviews diffs for correctness bugs.
            model: gpt-5.2-codex
            ---

            You are a code reviewer. Report only verified findings.
            """);

        Assert.Equal(Normalize(""""
            name = "code-reviewer"
            description = "Reviews diffs for correctness bugs."
            model = "gpt-5.2-codex"
            developer_instructions = """
            You are a code reviewer. Report only verified findings.
            """
            """"), Normalize(output));
    }

    [Fact]
    public void CodexAgentGoldenFallsBackToManifestMetadata()
    {
        // No frontmatter at all: identity comes from the asset manifest so the
        // required TOML fields are always present.
        var asset = TestData.Asset(AssetKind.Agents, "grill-me");
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Agents, Path.Combine(".codex", "agents", "grill-me.toml"), InstallMode.ConvertFile, IsFileTarget: true);
        var output = FileConverter.Convert(asset, target, "Challenge every plan with critical questions.\n");

        Assert.StartsWith("name = \"grill-me\"", output);
        Assert.Contains("description = ", output);
        Assert.Contains("Challenge every plan with critical questions.", output);
    }

    [Fact]
    public void CodexAgentGoldenEscapesTomlDelimiters()
    {
        var asset = TestData.Asset(AssetKind.Agents, "tricky");
        var target = new InstallTarget(ProviderName.Codex, AssetKind.Agents, Path.Combine(".codex", "agents", "tricky.toml"), InstallMode.ConvertFile, IsFileTarget: true);
        var output = FileConverter.Convert(asset, target, "Use \\n literally and never emit \"\"\" unescaped.\n");

        Assert.Contains("\\\\n literally", output);
        Assert.DoesNotContain("never emit \"\"\" unescaped", output);
    }

    [Fact]
    public void ConvertFileInstallWritesBackupAndRoundTrips()
    {
        using var temp = new TempDir();
        var asset = TestData.WriteLocalAsset(temp.Path, AssetKind.Rules, "ts-style",
            files: new Dictionary<string, string>
            {
                ["ts-style.mdc"] = "---\ndescription: TS rules.\nglobs: \"*.ts\"\n---\n\nRule body.\n"
            });
        var sourcePath = Path.Combine(temp.Path, "assets", "rules", "ts-style", "content", "ts-style.mdc");
        var targetPath = Path.Combine(temp.Path, ".claude", "rules", "ts-style.md");
        var target = new InstallTarget(ProviderName.Claude, AssetKind.Rules, Path.Combine(".claude", "rules", "ts-style.md"), InstallMode.ConvertFile, IsFileTarget: true);

        var first = FileConverter.Apply(asset, sourcePath, target, targetPath, _ => { });
        Assert.Equal(ContentHash.Compute(targetPath), first.Checksum);

        // Re-applying over our own output must produce identical content (idempotent).
        var backups = new List<string>();
        var second = FileConverter.Apply(asset, sourcePath, target, targetPath, backups.Add);
        Assert.Equal(first.Checksum, second.Checksum);
        Assert.Equal([targetPath], backups);
    }

    private static string MergeMcp(ProviderName provider, InstallScope scope, string relativeTarget)
    {
        using var temp = new TempDir();
        var asset = TestData.Asset(AssetKind.Mcp, "github", mcp: GitHubMcp);
        var targetPath = Path.Combine(temp.Path, relativeTarget);
        var target = new InstallTarget(provider, AssetKind.Mcp, relativeTarget, InstallMode.MergeMcp);
        McpMerger.Apply(asset, null, target, targetPath, scope, _ => { });
        return File.ReadAllText(targetPath);
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Trim().TrimStart('﻿');
}
