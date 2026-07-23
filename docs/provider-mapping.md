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
| agents | `.claude/agents/<id>.md` | `~/.claude/agents/<id>.md` | single file (markdown + frontmatter subagent); see [subagents docs](https://code.claude.com/docs/en/sub-agents) |
| rules | `.claude/rules/<id>.md` | `~/.claude/rules/<id>.md` | converted from the catalog `.mdc`: `globs` → `paths` list, `alwaysApply: true` → no `paths` (always loaded) |

## Codex

| Kind | Project scope | User scope | Mechanism |
|---|---|---|---|
| skills | `.agents/skills/<id>/` | `~/.agents/skills/<id>/` | copy tree (cross-tool `.agents` convention) |
| mcp | `.codex/config.toml` | `~/.codex/config.toml` | TOML merge, `[mcp_servers.<name>]` sections |
| hooks | `.codex/hooks.json` + `.codex/hooks/<id>/` | `~/.codex/hooks.json` + `~/.codex/hooks/<id>/` | JSON merge — Claude-style structure (PascalCase events, `matcher`, `timeout`); see [Codex hooks docs](https://developers.openai.com/codex/hooks) |
| instructions | `AGENTS.md` | `~/.codex/AGENTS.md` | single file |
| prompts | `.codex/prompts/<id>.md` | `~/.codex/prompts/<id>.md` | single file (custom prompt) |
| agents | `.codex/agents/<id>.toml` | `~/.codex/agents/<id>.toml` | **generated TOML** — the agent markdown becomes `name`, `description`, `developer_instructions` (body), optional `model`; see [Codex subagents docs](https://developers.openai.com/codex/subagents) |
| rules | — use instructions | — | unsupported |

## GitHub Copilot

| Kind | Project scope | User scope | Mechanism |
|---|---|---|---|
| skills | `.github/skills/<id>/` | `~/.copilot/skills/<id>/` | copy tree (VS Code agent skills / Copilot CLI) |
| mcp | `.vscode/mcp.json` — root key **`servers`** | `~/.copilot/mcp-config.json` — root key `mcpServers` | JSON merge |
| hooks | `.github/hooks/<id>.json` + `.github/hooks/<id>/` | `~/.copilot/hooks/<id>.json` + `~/.copilot/hooks/<id>/` | one JSON file per hook (`version: 1`, camelCase events, `bash`/`powershell` commands, `timeoutSec`); a `hook.ps1` twin next to `hook.sh` is registered automatically for Windows; see [Copilot CLI hooks docs](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/use-hooks) |
| instructions | `.github/instructions/<id>.instructions.md` | — managed in the editor | single file |
| prompts | `.github/prompts/<id>.prompt.md` | — managed in the editor | single file |
| agents | `.github/agents/<id>.agent.md` | `~/.copilot/agents/<id>.agent.md` | single file — the `.agent.md` suffix is **required** in both scopes (plain `.md` is silently ignored); user-level wins on a name collision; see [custom agents docs](https://docs.github.com/en/copilot/how-tos/copilot-cli/customize-copilot/create-custom-agents-for-cli) |
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
| agents | `.cursor/agents/<id>.md` | `~/.cursor/agents/<id>.md` | single file — Cursor discovers agents only at the folder root, never in subdirectories; see [subagents docs](https://cursor.com/docs/subagents) |

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

The `tool` matcher is written only for the tool events (`preToolUse`/`postToolUse`, defaulting to `Bash` when unset). Other triggers get no matcher unless the asset sets one explicitly — Claude Code matches `SessionStart` groups on the session source, never a tool name, so a defaulted tool matcher there would keep the hook from firing.

After installing a **Copilot repo-level hook**, the CLI reminds you to commit `.github/hooks/` — Copilot CLI picks the file up immediately, but the Copilot cloud coding agent reads hooks only from the default branch. The same applies to repo-level custom agents in `.github/agents/`.

## Agents — one canonical file, two dialects

An agent asset is a single markdown file with frontmatter (`name`, `description`, optional `model`) and the agent's instructions as the body — the shared subset that Claude Code, Cursor, and Copilot all read natively (unknown frontmatter fields are ignored by products that don't use them). Copilot only differs in the required `.agent.md` filename. Codex is the exception: its agents are TOML, so the install converts the markdown (frontmatter `name`/`description`/`model` plus the body as `developer_instructions`) instead of copying it. Both converted targets (`codex agents`, `claude rules`) are whole files owned by agentpack — drift detection, backups, and removal work exactly like any other single-file install.

## Considered and deliberately not mapped

These provider features were audited and intentionally left out, so their absence is a decision, not an oversight:

- **Claude Code plugins / marketplaces** — a competing distribution mechanism; agentpack is the distribution layer.
- **Claude Code output styles** — deprecated upstream.
- **Claude Code `settings.json` permission policies** — a possible future "policy" kind; needs its own merge semantics.
- **Copilot `.github/chatmodes/*.chatmode.md`** — superseded by `.github/agents/*.agent.md` custom agents.
- **Generating `agents/openai.yaml` inside skills** — Codex's optional per-skill extras (desktop-app UI metadata, invocation policy, MCP tool deps). Skills that ship one keep it byte-for-byte (skills install as whole trees); agentpack never generates one, since `SKILL.md` alone is sufficient everywhere and invented invocation policy would change unreviewed behavior.
- **`copilot-setup-steps.yml`, `.cursor/environment.json`, ignore files (`.cursorignore`, …)** — repo-specific one-offs, not shareable catalog assets.

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

Because Copilot CLI performs no placeholder expansion, a **remote (HTTP/SSE) server with header env vars is rejected in Copilot user scope** — a literal `${VAR}` would be sent to the server as the header value. Install such servers at project scope, where VS Code expands `${env:VAR}`.

Out-of-the-box friction each product adds by design (the CLI prints these as "next step" hints after apply): Claude Code asks the user to approve project `.mcp.json` servers on next start; the Copilot cloud coding agent reads repo hooks only from the default branch; MCP env vars must exist in the user's shell.

## Merge safety guarantees

- Merges never remove or rewrite entries the user already has; an existing server/hook with **different** content is a conflict error (exit code 3), never a silent overwrite.
- Identical re-installs are no-ops (idempotent).
- Any file agentpack modifies is backed up first (`.agentpack/backups/<timestamp>/`).
- Values in `env:` maps are rejected at validation — env vars are names, never secrets.

## Keeping this table honest

Provider formats move. When a provider changes its layout:

1. Update the adapter and this document in the same PR.
2. Update the matrix test (`ProviderAdapterTests`) and the golden merge tests (`MergerGoldenTests`).
3. Note the change in `CHANGELOG.md` if installed paths move.
