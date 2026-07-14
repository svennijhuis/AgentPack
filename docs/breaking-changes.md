# Breaking Changes

## External asset contracts

- External hooks now require an explicit `hook:` manifest section. `agentpack import --kind hooks` scaffolds it, with optional `--hook-trigger`, `--hook-tool`, `--hook-command`, and `--hook-timeout` overrides.
- External MCP used as an agent dependency requires typed `mcp:` metadata with an explicit `tools` inventory. Direct MCP imports may still use a root `mcp.json`.
- Fetched external agents, skills, hooks, MCP, instructions, prompts, and rules are validated against their kind-specific content shape before caching or installation.
- `agentpack import --kind tools|templates` now fails immediately because those kinds have no provider-native external installation contract.

## 0.3.0 — native agents, dependency ownership, transactional drift

### Catalog schema

- `agents` is a first-class asset kind. Its `agent.imports.instructions`, `.skills`, and `.mcp` lists are typed references with optional semantic-version ranges.
- Agent MCP imports require an explicit `mcp.tools` inventory. Generic `tools` assets are rejected; package custom tools as MCP.
- External assets now fail validation unless both pinned and checksummed in `catalog.lock.yaml`. External agent frontmatter is stripped and cannot add undeclared dependencies.

### Install lock and updates

- Entries add `direct`, `requiredBy`, `renderFingerprint`, `managedSnapshotPath`, and an optional hook `supportChecksum`. The managed snapshot lets interactive diff show the last generated version, local version, and new candidate; the support checksum detects edits to installed hook executables as well as registrations. Missing fields in older lock entries remain backward-compatible, and missing `direct` is interpreted as `true`.
- Dependency checksums participate in an agent's render fingerprint. `outdated` can therefore report an agent even when its own version did not change.
- Agent descriptions are now required. AgentPack no longer supports model overrides: imported frontmatter and legacy `agent.models` values are ignored with a warning and omitted from every generated file, which inherits the user's session or workflow model.
- Removing an agent leaves shared automatic dependencies as tracked orphans. Use `agentpack prune` to preview and remove clean orphans; locally modified orphans are retained.

### Drift and transactions

- `--yes` no longer implies a local-change choice. Non-interactive drift exits `3` unless `--force` or `--keep-local` is explicit.
- Keeping a locally modified generated agent skips its complete dependency closure. Existing unmanaged targets conflict unless byte-identical (adopt) or an explicit drift flag is supplied.
- Apply is transactional: sources and generated files stage first, the lock is saved last, and any apply failure restores all affected files and the previous lock.

## 0.2.0 — schema simplification, typed CLI, bundles removed

### Catalog schema

- **Bundles are removed.** Profiles carry asset lists directly. Old catalogs still load: bundle assets are folded into the profiles that referenced them, with a warning. Migrate by moving the asset lists into profiles and deleting `bundles:`.
- **Checksums moved out of manifests.** `source.checksum` is optional; the generated `catalog.lock.yaml` (from `agentpack catalog lock`, run in CI) is the normal home for content hashes. The old `agentpack checksum` command is gone.
- **`source.path` is gone for local assets.** Content always lives in `content/` next to `agentpack.yaml`. (Manifests that still set `path:` keep working.)
- **External shorthand.** `source: <url>@<ref>` replaces the `type/url/ref` block. The long form still works and is required when recording `license:`.
- **`providers` omitted now means all providers** (previously it meant none/explicit only). Assets restricted to specific providers must say so explicitly.
- **`owner` is optional.** Ownership is normally governed by CODEOWNERS on `assets/**`.
- Unknown kinds, statuses, channels, providers, and malformed versions are now **load errors** with precise messages (previously partially ignored).

### CLI

- `agentpack new --external-url/--version` and `agentpack catalog import` are replaced by **`agentpack import <url@ref>`**. `--ref` remains for URLs without the suffix; `--version` as a ref alias is gone.
- Group shortcut flags (`--backend`, `--frontend`, ...) are gone — use `-g/--group <name>`.
- `agentpack add bundle <id>` is gone (bundles removed) — use profiles or `-g`.
- `plan add`/`plan upgrade` subcommands became `agentpack plan ...` (add dry-run) and `agentpack outdated` (upgrade dry-run).
- Locally-modified installs are no longer silently skipped on apply: the CLI prompts (overwrite / keep / diff / abort), or honors `--force` / `--keep-local` / `--yes` in scripts.

### Provider mappings (audited against product docs)

- Hooks now install for **all four providers**, each in its real format:
  Claude `.claude/settings.json`, Codex `.codex/hooks.json` (Claude-style, PascalCase events),
  Copilot one file per hook at `.github/hooks/<id>.json` (`version: 1`, `bash`/`powershell`, `timeoutSec`),
  Cursor `.cursor/hooks.json` (`version: 1`, camelCase events, `timeout`).
- Copilot pre-tool hooks receive a generated compatibility wrapper: portable exit code `2` is translated to Copilot's native structured deny response because Copilot otherwise treats `2` as a warning.
- Codex rules (`.codex/rules/*.rules`) removed — invented path; use instructions (AGENTS.md).
- Copilot project MCP now merges into `.github/mcp.json` (root key `mcpServers`) instead of writing a manual payload under `.agentpack/copilot-mcp/`. This supersedes the earlier VS Code-specific `.vscode/mcp.json` mapping.
- Cursor skills moved from `.agents/skills/` to `.cursor/skills/`.
- Cursor file-based rules and commands are project-scoped; user rules/commands are managed in Cursor rather than written to undocumented home-directory paths.
- Claude prompts now install as slash commands (`.claude/commands/<id>.md`).

### Lockfiles

- Install lockfile entries are typed; the obsolete `manualApplyRequired` field is ignored. Entries recorded by ≤0.1.0 with `installMode: "Manual"` cannot be parsed — remove `.agentpack/lock.json` and re-run `agentpack add`.

## Policy

Breaking changes to the catalog schema bump `schemaVersion` and ship with a migration note here. Deprecated fields keep working for at least two minor releases with warnings before removal.
