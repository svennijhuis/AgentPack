# Breaking Changes

## 0.2.0 — schema simplification, typed CLI, bundles removed

### Catalog schema

- **Bundles are removed.** Profiles carry asset lists directly. Old catalogs still load: bundle assets are folded into the profiles that referenced them, with a warning. Migrate by moving the asset lists into profiles and deleting `bundles:`.
- **Checksums moved out of manifests.** `source.checksum` is optional; the generated `catalog.lock.yaml` (from `agentpack catalog lock`, run in CI) is the normal home for content hashes. The old `agentpack checksum` command is gone.
- **`source.path` is gone for local assets.** Content always lives in `content/` next to `agentpack.yaml`. (Manifests that still set `path:` keep working.)
- **External shorthand.** `source: <url>@<ref>` remains parse-compatible for migration, but valid external assets now use the mapping form so they can record `license:`.
- **External provenance is visible on add.** The repository URL is the attribution; install plans show its compact repository name and single-asset installs confirm `Add <asset> from <repository>?`. License metadata remains optional and warns when omitted.
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
- Codex rules (`.codex/rules/*.rules`) removed — invented path; use instructions (AGENTS.md).
- Copilot project MCP now merges into `.vscode/mcp.json` (root key `servers`) instead of writing a manual payload under `.agentpack/copilot-mcp/`.
- Cursor skills moved from `.agents/skills/` to `.cursor/skills/`.
- Claude prompts now install as slash commands (`.claude/commands/<id>.md`).

### Lockfiles

- Install lockfile entries are typed; the obsolete `manualApplyRequired` field is ignored. Entries recorded by ≤0.1.0 with `installMode: "Manual"` cannot be parsed — remove `.agentpack/lock.json` and re-run `agentpack add`.

## Policy

Breaking changes to the catalog schema bump `schemaVersion` and ship with a migration note here. Deprecated fields keep working for at least two minor releases with warnings before removal.
