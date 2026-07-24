# Changelog

## 0.3.2 — 2026-07-24

### Changed

- **Redesigned the install/update picker.** The current row is bold and accent-colored
  with a pointer, so it is obvious where the cursor sits during arrow navigation; arrow
  keys wrap around; and rows are aligned into single lines that no longer wrap a trailing
  tag onto its own line.

### Removed

- **Per-asset status and channel.** The `status` (experimental/recommended/deprecated/
  blocked) and `channel` (internal/beta/stable) fields are gone — every catalog asset is
  stable and installs the same way. This drops the `(experimental)` picker tag, the `list`
  Status column, and the block/deprecate install gate. Group deprecation in `catalog.yaml`
  is unaffected; leftover `status:`/`channel:` keys in manifests are ignored.

## 0.3.1 — 2026-07-24

### Added

- **`agentpack config`.** Shows every path AgentPack uses — home, config file,
  catalog cache, user lockfile, and provider home — and where each is resolved from.
  `--set-home <path>` persists a new state directory; `--reset-home` reverts to the
  default. Home precedence is `AGENTPACK_HOME` → persisted choice → `~/.agentpack`.
  Provider files (`.claude/`, `.codex/`, …) are never relocated.
- **Install/update picker shows install state.** Rows already present are marked, and
  ones with a newer catalog version show `(update available)`, so the picker makes it
  clear whether Enter will install or update.
- **`agentpack list --wide` / `search --wide`.** Opt in to the description, groups,
  and providers columns.

### Changed

- **Compact `list` output.** The default view drops the description column (it wrapped
  into a wall of text) and shows only the columns that carry a non-default value: the
  kind column collapses when every row shares it, and source waits for a wide terminal
  or `--wide`. A footer lists the kinds present and how to install what you see.
- Documentation reorganized around a complete command reference; examples no longer use
  `grill-me`.

## 0.3.0 — 2026-07-23

Catalog-first workflow and safety cleanup.

### Added

- **Seeded the official catalog across every installable kind.** It now ships 60
  assets — skills, hooks, MCP servers, agents, prompts, rules, and instructions —
  instead of three examples. External assets are pinned to commit SHAs and
  checksummed; sources are permissively licensed only (MIT / CC0). Upstream:
  `mattpocock/skills`, `obra/superpowers`, `github/awesome-copilot`,
  `wshobson/agents`, `wshobson/commands`, `PatrickJS/awesome-cursorrules`,
  `microsoft/azure-skills`, `shadcn-ui/ui`, `vercel-labs/skills`, and
  `JuliusBrussee/caveman`. A coverage test keeps every installable kind populated.

- **One built-in catalog.** Normal users can search and install without configuring
  a repository. Install and update refresh it automatically; status shows the URL,
  branch, revision, cache, and last refresh.
- **`agentpack submit`.** Local files/folders and pinned external URLs become a
  validated proposal branch and pull request. Read-only contributors automatically
  submit through a GitHub fork; maintainers still use a non-main proposal branch.
- **Kind-specific submission guardrails.** Skills require `SKILL.md`; hooks accept a
  file or folder with a safe relative command; MCP servers use typed command/URL
  settings and environment-variable names only. Local content is previewed, bounded,
  and checked for symlinks and secret-like files before publishing.
- Catalog manifests can declare `minimumAgentPackVersion` so a newer catalog gives
  an actionable CLI update message.
- CODEOWNERS and a documented branch-protection/ruleset checklist establish the
  catalog review boundary.

### Changed

- The public command surface is now `search`, `install`, `update`, and `submit`.
  The discarded local catalog, overlay, manual import, and compatibility-alias paths
  were removed instead of remaining as competing workflows.
- Catalog content updates independently from NuGet. A NuGet release is required only
  for CLI behavior, provider adapters, validation, or catalog schema changes.

### Fixed

- **CLI printed nothing in some non-TTY environments.** Hosts that report a
  negative console width (certain containers, `docker exec`, `TERM=linux`
  shells) made every line render as an ellipsis while the exit code stayed 0.
  Non-positive widths now fall back to 80 columns.
- **External assets could install with no integrity check.** A missing checksum
  now warns for SHA-pinned assets and fails validation for tag-pinned ones —
  a moved tag with no checksum was previously undetectable. The repo's own
  `catalog.lock.yaml` gained the missing external checksum.
- **Raw `mcpServers` passthrough bypassed the no-secrets rule.** A
  `content/mcp.json` shipping a whole `mcpServers` object was installed
  verbatim; literal env values are now rejected and declared vars rewritten to
  each provider's placeholder syntax, same as every other path.
- **Codex + Cursor collided on repo-root `AGENTS.md`.** Removing one provider's
  instructions no longer deletes the file while the other provider's install
  still references it, and adding the second provider no longer flags the
  first's file as unmanaged.
- **Concurrent runs could corrupt the cache.** The external-asset cache and
  source clones are now serialized with the same lock used for scopes.
- **`catalog.lock.yaml` robustness.** Malformed YAML fails with an actionable
  error instead of an internal one; saves are atomic.
- Copilot per-hook files honor the drift decision instead of overwriting
  content agentpack did not write; same-named files backed up in the same
  millisecond no longer overwrite each other's backup; source cache paths
  compare case-insensitively on Windows; sanitized source names cannot collide.
- **Hooks on non-tool triggers fired never.** `stop`, `sessionStart`,
  `userPromptSubmit`, and `notification` hooks were registered on Claude and
  Codex inside a defaulted `"matcher": "Bash"` group, which those events never
  match. The tool-matcher default now applies only to `preToolUse`/`postToolUse`;
  other triggers get no matcher unless the asset sets one.
- **Copilot user-scope remote MCP servers** with header env vars now fail with
  a hint instead of writing `${VAR}` placeholders Copilot CLI never expands
  (the literal string was sent as the header value).
- **TOML escaping**: control characters in MCP commands/args no longer produce
  invalid `.codex/config.toml`.
- A PowerShell hook twin in a subfolder of `content/` is registered at its real
  installed path instead of a same-directory guess.

### Tests

- The provider × kind × scope matrix now pins user-scope behavior (including
  the Copilot/Cursor "managed in the editor" cases), every workspace-detection
  marker, all hook trigger translations per provider, Codex TOML edge cases
  (quoted server ids, escaping, args/env arrays), remote-server header syntax
  per provider, and real on-disk landings for user scope, Cursor rules,
  prompts, and Copilot's `.instructions.md`/`.prompt.md` naming.

### Changed

- Releases now gate on the same three-OS test matrix as CI, attach a
  `SHA256SUMS` file, and publish to NuGet.org through trusted publishing without
  storing a long-lived API key.
- CI validates the catalog and re-verifies external assets at their pinned
  refs on every run.

## Pre-release development — 2026-07-12

Historical command names below describe the prototype at that time and are not supported by the current CLI. See the unreleased section and README for the catalog-first interface.

Initial development milestone. Everything from the production-readiness review is fixed,
regression-tested, and CI-verified on Linux, Windows, and macOS.

### Fixed

- **False drift in shared provider configs.** The lockfile now records the exact
  fragment each asset merged into a shared config file (`.claude/settings.json`,
  `.mcp.json`, `.codex/config.toml`, ...), and drift detection compares only that
  fragment. Installing multiple hooks or MCP servers no longer marks earlier
  installs as "modified locally" — which previously caused `--yes`/CI runs to
  silently skip their upgrades.
- **Upgrades of merged entries.** Upgrading an MCP asset whose config changed used
  to fail with a conflict; upgrading a hook used to stack a duplicate handler.
  Both now replace the previously installed fragment in place.
- **`agentpack remove` un-merges.** Removing a hook or MCP asset now deletes the
  registration agentpack added to shared configs instead of leaving a dangling
  reference to a deleted script (which broke the provider on every tool call).
  Everything else in those files is left untouched.
- **Unmanaged files are protected.** A pre-existing, hand-written target (for
  example `CLAUDE.md`) is only overwritten when the drift decision says so —
  a prompt interactively, `--force` in scripts. With `--yes` it is kept, with a
  warning. Existing shared config files are not treated as unmanaged for merges.
- **Git option injection closed.** Git is invoked with an argument list and `--`
  separators, and branches/refs/URLs starting with `-` are rejected (a branch
  like `--upload-pack=...` could previously reach command execution).
- **`AGENTPACK_HOME` no longer relocates provider configs.** It moves agentpack's
  own state only; `.claude/`, `.codex/`, etc. always resolve against the real
  user profile in user scope.
- **Config parsing.** A corrupt provider config now fails with an actionable
  message instead of an internal error; JSONC (comments, trailing commas) in
  `.vscode/mcp.json` is tolerated; merged files are written without a UTF-8 BOM.

### Added

- `agentpack --version`, and the version in `agentpack doctor`.
- Synced catalogs auto-refresh when older than 24 hours, falling back to the
  cached copy with a warning when offline.
- Backups under `.agentpack/backups/` are pruned to the newest 20.
- Lockfile and config files preserve fields written by newer agentpack versions
  (safe downgrade/upgrade between 1.x versions).
- CI verifies formatting (`dotnet format`) and builds proposal branches.

### Upgrading from 0.2.x

No manual migration. Lock entries written by 0.2.x lack the recorded fragment;
the first `agentpack install`/`update`/`profile apply` that touches an up-to-date
entry backfills it automatically. Until an entry is backfilled it is treated as
clean (never as false drift). If an upgrade of a 0.2.x-installed MCP server or
hook reports a conflict because its config changed between versions, rerun with
`--force` once.

## 0.2.0 — 2026-07-02

Typed core, Spectre CLI, verified provider mappings, catalog lock, external
assets pinned to SHAs, and hooks on all four providers. The current catalog-first
workflow and design rationale are documented in `README.md` and `docs/how-it-works.md`.
