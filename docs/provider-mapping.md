# Provider Mapping

The audited provider × kind matrix. The adapters in `src/AgentPack.Core/Providers/` implement exactly this table, and `tests/AgentPack.Tests/ProviderAdapterTests.cs` pins every cell — changing a path is a deliberate, reviewed decision.

**Rule: no invented paths.** If a product has no concept for a kind, the adapter returns `Unsupported` with a reason, and the plan output shows an explicit skip. An install that "succeeds" into a file nothing reads is worse than an honest skip.

Project-scope paths are relative to the repo root. User-scope paths are relative to the user's home directory.

## Claude Code

| Kind | Project scope | User scope | Mechanism |
|---|---|---|---|
| agents | `.claude/agents/<id>.md` | `~/.claude/agents/<id>.md` | generated Markdown; imported skills in native `skills` metadata; agent-local MCP |
| skills | `.claude/skills/<id>/` | `~/.claude/skills/<id>/` | copy tree |
| hooks | `.claude/settings.json` + `.claude/hooks/<id>/` | `~/.claude/settings.json` + `~/.claude/hooks/<id>/` | JSON merge (`hooks.<Event>[].hooks[]`, PascalCase events, `matcher`) |
| mcp | `.mcp.json` | `~/.claude.json` | JSON merge under `mcpServers` |
| instructions | `CLAUDE.md` | `~/.claude/CLAUDE.md` | single file |
| prompts | `.claude/commands/<id>.md` | `~/.claude/commands/<id>.md` | single file (slash command) |
| rules | `.claude/rules/<id>.md` | `~/.claude/rules/<id>.md` | single rule file; supports native `paths` frontmatter when supplied by the asset |

## Codex

| Kind | Project scope | User scope | Mechanism |
|---|---|---|---|
| agents | `.codex/agents/<id>.toml` | `~/.codex/agents/<id>.toml` | generated TOML with private composed instructions and agent-local MCP sections |
| skills | `.agents/skills/<id>/` | `~/.agents/skills/<id>/` | copy tree (cross-tool `.agents` convention) |
| mcp | `.codex/config.toml` | `~/.codex/config.toml` | TOML merge, `[mcp_servers.<name>]` sections |
| hooks | `.codex/hooks.json` + `.codex/hooks/<id>/` | `~/.codex/hooks.json` + `~/.codex/hooks/<id>/` | JSON merge — Claude-style structure (PascalCase events, `matcher`, `timeout`); see [Codex hooks docs](https://developers.openai.com/codex/hooks) |
| instructions | `AGENTS.md` | `~/.codex/AGENTS.md` | single file |
| prompts | — use a skill | `~/.codex/prompts/<id>.md` | user-only custom prompt (deprecated by Codex; skills are preferred) |
| rules | — use instructions | — | unsupported |

## GitHub Copilot

| Kind | Project scope | User scope | Mechanism |
|---|---|---|---|
| agents | `.github/agents/<id>.agent.md` | `~/.copilot/agents/<id>.agent.md` | generated Markdown; project file is directly importable by Agentic Workflows |
| skills | `.github/skills/<id>/` | `~/.copilot/skills/<id>/` | copy tree (VS Code agent skills / Copilot CLI) |
| mcp | `.github/mcp.json` | `~/.copilot/mcp-config.json` | JSON merge under `mcpServers`; Copilot CLI workspace/user config |
| hooks | `.github/hooks/<id>.json` + `.github/hooks/<id>/` | `~/.copilot/hooks/<id>.json` + `~/.copilot/hooks/<id>/` | one JSON file per hook (`version: 1`, camelCase events, `bash`/`powershell` commands, `timeoutSec`); a `hook.ps1` twin next to `hook.sh` is registered automatically for Windows; see [Copilot CLI hooks docs](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks) |
| instructions | `.github/copilot-instructions.md` | `~/.copilot/copilot-instructions.md` | repository-wide / Copilot CLI user instructions |
| prompts | `.github/prompts/<id>.prompt.md` | — managed in the editor | single file |
| rules | — use instructions | — | unsupported |

## Cursor

| Kind | Project scope | User scope | Mechanism |
|---|---|---|---|
| agents | `.cursor/agents/<id>.md` | `~/.cursor/agents/<id>.md` | generated Markdown; subagent inherits MCP from `.cursor/mcp.json` |
| skills | `.cursor/skills/<id>/` | `~/.cursor/skills/<id>/` | copy tree |
| rules | `.cursor/rules/<id>.mdc` | — managed in the app (User Rules) | single project rule file |
| hooks | `.cursor/hooks.json` + `.cursor/hooks/<id>/` | `~/.cursor/hooks.json` + `~/.cursor/hooks/<id>/` | JSON merge (`version: 1`, event arrays) |
| mcp | `.cursor/mcp.json` | `~/.cursor/mcp.json` | JSON merge under `mcpServers` |
| instructions | `AGENTS.md` | — managed in the app (User Rules) | single file |
| prompts | `.cursor/commands/<id>.md` | — managed in Cursor | single project command file |

## Agent dependency rendering

| Import | Claude | Codex | Copilot | Cursor |
|---|---|---|---|---|
| instructions | embedded privately in generated prompt | embedded | embedded | embedded |
| skills | installed first; listed in native metadata | installed in `.agents/skills`; required ids named in prompt | installed in `.github/skills` / `~/.copilot/skills`; ids named in prompt | installed in `.cursor/skills`; ids named in prompt |
| MCP | agent-local frontmatter configuration | agent-local TOML sections | agent-local frontmatter configuration | merged once into `.cursor/mcp.json` because custom subagents inherit parent MCP servers |

An explicit portable tool allowlist is translated exactly for Claude (`Read`, `Glob`/`Grep`, `Edit`/`Write`, `Bash`, `WebFetch`/`WebSearch`, `Agent`) and Copilot (`read`, `search`, `edit`, `execute`, `web`, `agent`). Codex and Cursor do not expose an equivalent granular per-agent field: AgentPack sets their native read-only safety control when both `edit` and `execute` are absent and composes the declared policy into the prompt, but otherwise the agent inherits the parent tool surface. Imported MCP tools are appended using native names. MCP environment-variable **names** are rendered, never values. Copilot's `web` alias is currently ignored by the GitHub cloud coding agent, although other Copilot surfaces can use it.

Model metadata is never rendered. Imported frontmatter and the legacy `agent.models` mapping are stripped with a warning, so every native agent inherits the model selected by its user, session, or workflow. Full examples are in [agent-authoring.md](agent-authoring.md).

GitHub Agentic Workflow reuse needs no second render:

```yaml
imports:
  - .github/agents/dotnet-upgrade.agent.md
```

AgentPack prints required skills, MCP tools, environment names, and the workflow import after installation. It deliberately does not generate workflow triggers, permissions, or safe-output policy.

## Hook trigger mapping

Catalog triggers are normalized (`preToolUse`, `postToolUse`, `stop`, `sessionStart`, `userPromptSubmit`, `notification`) and translated per provider. All four providers have hook systems:

| Catalog trigger | Claude Code | Codex | Copilot CLI | Cursor |
|---|---|---|---|---|
| preToolUse | `PreToolUse` | `PreToolUse` | `preToolUse` | `preToolUse` |
| postToolUse | `PostToolUse` | `PostToolUse` | `postToolUse` | `postToolUse` |
| stop | `Stop` | `Stop` | `agentStop` | `stop` |
| sessionStart | `SessionStart` | `SessionStart` | `sessionStart` | `sessionStart` |
| userPromptSubmit | `UserPromptSubmit` | `UserPromptSubmit` | `userPromptSubmitted` | `beforeSubmitPrompt` |
| notification | `Notification` | — (error with hint) | `notification` | — (error with hint) |

After installing a **Copilot repo-level hook**, the CLI reminds you to commit `.github/hooks/` — Copilot CLI picks the file up immediately, but the Copilot cloud coding agent reads hooks only from the default branch.

## MCP environment variables — per-target syntax

Secrets never enter the catalog: MCP env vars are declared **by name** in the manifest and rendered in whatever reference syntax the target actually expands (verified per product):

| Target | File | Env reference |
|---|---|---|
| Claude Code | `.mcp.json` / `~/.claude.json` | `"TOKEN": "${TOKEN}"` |
| Copilot (project) | `.github/mcp.json` | `"TOKEN": "${TOKEN}"`; `tools: ["*"]` is written when no narrower inventory is declared |
| Copilot CLI (user) | `~/.copilot/mcp-config.json` | `"TOKEN": "${TOKEN}"`; `tools: ["*"]` is written when no narrower inventory is declared |
| Cursor | `.cursor/mcp.json` | `"TOKEN": "${env:TOKEN}"` |
| Codex | `.codex/config.toml` | `env_vars = ["TOKEN"]` (forwarded from the shell); HTTP headers via `env_http_headers = { Header = "TOKEN" }` |

Note: Copilot CLI loads trusted workspace MCP from `.github/mcp.json`. GitHub.com repository MCP for the cloud agent/code review is administered in repository settings; agent-local MCP remains embedded in generated `.github/agents/*.agent.md` files.

Out-of-the-box friction each product adds by design (the CLI prints these as "next step" hints after apply): Claude Code asks the user to approve project `.mcp.json` servers on next start; the Copilot cloud coding agent reads repo hooks only from the default branch; MCP env vars must exist in the user's shell.

## Merge safety guarantees

- Merges never remove or rewrite entries the user already has; an existing server/hook with **different** content is a conflict error (exit code 3), never a silent overwrite.
- Identical re-installs are no-ops (idempotent).
- Any file agentpack modifies is backed up first (`.agentpack/backups/<timestamp>/<path-key>/`), so equal basenames from different providers cannot collide.
- Values in `env:` maps are rejected at validation — env vars are names, never secrets.

Portable pre-tool hooks use exit code 2 as their denial contract. Claude Code,
Codex, and Cursor consume that directly. Copilot treats exit code 2 as a warning,
so AgentPack installs a small provider wrapper that converts it to Copilot's
successful structured response (`permissionDecision: "deny"`).

## Verification boundary

The provider matrix and generated config dialects are unit-tested. Unit tests can
prove deterministic translation, merge behavior, drift detection, path safety,
and wrapper behavior. They cannot prove provider login state, repository trust,
organization policy, feature flags, network access, or a future provider release.
Those remain runtime integration checks and are reported separately from unit-test
coverage.

## Keeping this table honest

Provider formats move. When a provider changes its layout:

1. Update the adapter and this document in the same PR.
2. Update the matrix test (`ProviderAdapterTests`) and the golden merge tests (`MergerGoldenTests`).
3. Note the change in `docs/breaking-changes.md` if installed paths move.
