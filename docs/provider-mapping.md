# Provider Mapping

The audited provider × kind matrix. The adapters in `src/AgentPack.Core/Providers/` implement exactly this table, and `tests/AgentPack.Tests/ProviderAdapterTests.cs` pins every cell — changing a path is a deliberate, reviewed decision.

**Rule: no invented paths.** If a product has no concept for a kind, the adapter returns `Unsupported` with a reason, and the plan output shows an explicit skip. An install that "succeeds" into a file nothing reads is worse than an honest skip.

Project-scope paths are relative to the repo root. User-scope paths are relative to the user's home directory.

## Claude Code

| Kind | Project scope | User scope | Mechanism |
|---|---|---|---|
| skills | `.claude/skills/<id>/` | `~/.claude/skills/<id>/` | copy tree |
| hooks | `.claude/settings.json` + `.claude/hooks/<id>/` | `~/.claude/settings.json` + `~/.claude/hooks/<id>/` | JSON merge (`hooks.<Event>[].hooks[]`, PascalCase events, `matcher`) |
| mcp | `.mcp.json` | `~/.claude.json` | JSON merge under `mcpServers` |
| instructions | `CLAUDE.md` | `~/.claude/CLAUDE.md` | single file |
| prompts | `.claude/commands/<id>.md` | `~/.claude/commands/<id>.md` | single file (slash command) |
| rules | — use instructions | — | unsupported |

## Codex

| Kind | Project scope | User scope | Mechanism |
|---|---|---|---|
| skills | `.agents/skills/<id>/` | `~/.agents/skills/<id>/` | copy tree (cross-tool `.agents` convention) |
| mcp | `.codex/config.toml` | `~/.codex/config.toml` | TOML merge, `[mcp_servers.<name>]` sections |
| hooks | `.codex/hooks.json` + `.codex/hooks/<id>/` | `~/.codex/hooks.json` + `~/.codex/hooks/<id>/` | JSON merge — Claude-style structure (PascalCase events, `matcher`, `timeout`); see [Codex hooks docs](https://developers.openai.com/codex/hooks) |
| instructions | `AGENTS.md` | `~/.codex/AGENTS.md` | single file |
| prompts | `.codex/prompts/<id>.md` | `~/.codex/prompts/<id>.md` | single file (custom prompt) |
| rules | — use instructions | — | unsupported |

## GitHub Copilot

| Kind | Project scope | User scope | Mechanism |
|---|---|---|---|
| skills | `.github/skills/<id>/` | `~/.copilot/skills/<id>/` | copy tree (VS Code agent skills / Copilot CLI) |
| mcp | `.vscode/mcp.json` — root key **`servers`** | `~/.copilot/mcp-config.json` — root key `mcpServers` | JSON merge |
| hooks | `.github/hooks/<id>.json` + `.github/hooks/<id>/` | `~/.copilot/hooks/<id>.json` + `~/.copilot/hooks/<id>/` | one JSON file per hook (`version: 1`, camelCase events, `bash`/`powershell` commands, `timeoutSec`); a `hook.ps1` twin next to `hook.sh` is registered automatically for Windows; see [Copilot CLI hooks docs](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks) |
| instructions | `.github/instructions/<id>.instructions.md` | — managed in the editor | single file |
| prompts | `.github/prompts/<id>.prompt.md` | — managed in the editor | single file |
| rules | — use instructions | — | unsupported |

## Cursor

| Kind | Project scope | User scope | Mechanism |
|---|---|---|---|
| skills | `.cursor/skills/<id>/` | `~/.cursor/skills/<id>/` | copy tree |
| rules | `.cursor/rules/<id>.mdc` | `~/.cursor/rules/<id>.mdc` | single file |
| hooks | `.cursor/hooks.json` + `.cursor/hooks/<id>/` | `~/.cursor/hooks.json` + `~/.cursor/hooks/<id>/` | JSON merge (`version: 1`, event arrays) |
| mcp | `.cursor/mcp.json` | `~/.cursor/mcp.json` | JSON merge under `mcpServers` |
| instructions | `AGENTS.md` | — managed in the app (User Rules) | single file |
| prompts | `.cursor/commands/<id>.md` | `~/.cursor/commands/<id>.md` | single file |

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
| VS Code Copilot (project) | `.vscode/mcp.json` | `"TOKEN": "${env:TOKEN}"` |
| Copilot CLI (user) | `~/.copilot/mcp-config.json` | no expansion syntax documented — the `env` object is omitted (stdio servers inherit your shell env) and `tools: ["*"]` is written explicitly |
| Cursor | `.cursor/mcp.json` | `"TOKEN": "${env:TOKEN}"` |
| Codex | `.codex/config.toml` | `env_vars = ["TOKEN"]` (forwarded from the shell); HTTP headers via `env_http_headers = { Header = "TOKEN" }` |

Note: Copilot CLI reads MCP servers only from its user-level config; the project-scope Copilot MCP install targets VS Code Copilot (`.vscode/mcp.json`), which the Copilot coding agent also understands.

## Merge safety guarantees

- Merges never remove or rewrite entries the user already has; an existing server/hook with **different** content is a conflict error (exit code 3), never a silent overwrite.
- Identical re-installs are no-ops (idempotent).
- Any file agentpack modifies is backed up first (`.agentpack/backups/<timestamp>/`).
- Values in `env:` maps are rejected at validation — env vars are names, never secrets.

## Keeping this table honest

Provider formats move. When a provider changes its layout:

1. Update the adapter and this document in the same PR.
2. Update the matrix test (`ProviderAdapterTests`) and the golden merge tests (`MergerGoldenTests`).
3. Note the change in `docs/breaking-changes.md` if installed paths move.
