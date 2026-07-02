# AgentPack — Production Readiness Plan

Status: **implemented**, 2026-07-02. All six phases landed; this document is kept as the design rationale.
Notable deviations from the proposal: `add external` shipped as `agentpack import`; the `checksum` command was replaced entirely by `agentpack catalog lock`.
Scope: everything needed to take AgentPack from working prototype to a tool a large organization can rely on.

## Verdict

The core design is right and worth keeping:

- **Catalog repo as source of truth**, assets contributed via PR — correct governance model for a big org.
- **Lockfile with checksums** per scope (project/user) — enables drift detection, pinning, safe upgrades.
- **Provider adapters** behind one interface — correct extension point for Claude/Codex/Copilot/Cursor and future providers.
- **External assets pinned to a commit SHA** — correct supply-chain posture (no floating branches).

What is not production-ready: stringly-typed domain model, hand-rolled argument parsing, a 650-line `Program.cs`, silent failure modes, unverified provider path mappings, YAML authoring friction, no interactivity, no CI/release pipeline. The plan below fixes these in phases; each phase ships independently.

---

## Phase 0 — Design decisions (do first, everything else builds on these)

### 0.1 Simplify the asset manifest (YAML maintainability)

Today the author must write `id`, `kind`, `source.path`, and `source.checksum` — all of which are derivable. New rule: **everything derivable is derived.**

- `id` — inferred from folder name (`assets/skills/grill-me/` → `grill-me`). Manifest may override; validator errors on mismatch.
- `kind` — inferred from parent folder (`assets/skills/...` → `skills`).
- `source.path` — gone. Convention: local content lives in `content/` next to `agentpack.yaml`. No exceptions.
- `source.checksum` — removed from the authored file. Computed by CI on merge and written to a generated `catalog.lock.yaml` in the catalog repo. Authors never touch hashes; `agentpack checksum` becomes a CI concern, not a human chore.

Minimal local asset manifest becomes:

```yaml
name: Grill Me
version: 1.0.0
description: Challenges a plan with critical review questions.
groups: [engineering, review]
providers: [codex, claude, cursor, copilot]
```

Minimal external asset manifest:

```yaml
name: PDF Review
version: 1.0.0
description: Reviews PDF documents.
providers: [codex, claude]
source: https://github.com/anthropics/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d
```

The `url@ref` shorthand replaces the `type/url/ref/version` block. The long form stays supported for extra fields (license, upstreamName). `@<tag>` is allowed only for annotated tags that CI resolves and pins to a SHA in `catalog.lock.yaml`.

### 0.2 Collapse the grouping model: groups + profiles, drop bundles

Three overlapping mechanisms (groups, bundles, profiles) is one too many. For a large org:

- **groups** — pure metadata/tags for discovery and filtering (`agentpack list --group backend`). No install semantics.
- **profiles** — the installable unit for teams ("backend team gets these assets on these providers"). Versioned.
- **bundles** — delete. Everything a bundle does, a profile with an asset list does. Migration: convert existing bundles to profiles; validator warns on `bundles:` key for two releases, then errors.

One question a new user must answer drops from "group, bundle, or profile?" to "profile = what my team installs, group = label."

### 0.3 Contribution flow is PR-based — confirm and lean into it

Yes: adding/changing assets happens in the catalog repo via PR. That is the review gate you want ("in the end I want a PR because somebody needs to check it"). The CLI's job is to make the PR trivial:

- `agentpack new skills grill-me` — scaffolds folder + manifest + content, prints "commit and open a PR".
- `agentpack add external <url>@<ref>` — scaffolds an external manifest (replaces both `new --external-url` and `catalog import`; one path, not two).
- Catalog repo CI runs `agentpack catalog validate` + checksum generation + license check on every PR. CODEOWNERS on `assets/**` routes review (security team owns `assets/hooks/**`, etc.).

### 0.4 Drop confusing CLI surface

- `--owner` — optional everywhere, defaulted from git config / CODEOWNERS. It's governance metadata, not something a user should decide at the prompt.
- `--version` as alias for `--ref` — delete. One flag: `--ref <sha|tag>`. Asset `version:` (the org-facing semver) and source `ref` (the upstream pin) are different concepts; the alias blurred them.
- `catalog import` — merged into `add external` (see 0.3).
- Hardcoded `--backend/--frontend/...` group shortcut flags — delete; they duplicate catalog data. `--group backend` is enough, and `-g backend` as short form.

---

## Phase 1 — Type safety and code structure

Goal: Rust-grade type safety within idiomatic C#. "Parse, don't validate": YAML/args are parsed once at the boundary into types that cannot represent invalid states; everything past the boundary is exhaustive over closed types.

### 1.1 Domain types (AgentPack.Core)

- `enum AssetKind { Skill, Hook, Mcp, Tool, Instruction, Rule, Prompt, Template }` — replaces `string Kind` + `KnownKinds`. Parsed at load; unknown kind = load error with location.
- `enum ProviderName { Claude, Codex, Copilot, Cursor }` — replaces provider strings in CLI, adapters, lockfile. One place to add a provider (compiler then forces every switch to handle it).
- `enum AssetStatus { Experimental, Recommended, Deprecated, Blocked }`, `enum Channel { Internal, Beta, Stable }`, `enum HookTrigger { PreToolUse, PostToolUse, ... }`.
- Discriminated union for source (sealed hierarchy + exhaustive switch — the C# equivalent of a Rust enum):

  ```csharp
  public abstract record AssetSource
  {
      public sealed record Local() : AssetSource;                       // content/ by convention
      public sealed record External(Uri Url, GitRef Ref, string? License) : AssetSource;
  }
  ```

- Value types: `AssetId`, `GroupId`, `ProfileId` (readonly record structs with validated construction), `SemVersion` (parsed struct, no `int.Parse` at compare time), `Sha256` (validated format).
- Mutable DTO classes only in a `Serialization/` namespace as the YamlDotNet target; mapped to immutable domain records with a `Result<T, CatalogError>`-style outcome. Enable `TreatWarningsAsErrors` + nullable strict.
- `InstallState` becomes an enum (`Available, Installed, UpdateAvailable, Pinned, LocalChanges, Missing, UnmanagedPresent, ManualApplyRequired`) instead of magic strings compared with `==`.

### 1.2 Project layout

```
src/
  AgentPack.Core/
    Catalog/        Catalog.cs, Asset.cs, Profile.cs, CatalogLoader.cs, CatalogValidator.cs, Serialization/
    Providers/      IProviderAdapter.cs, ClaudeAdapter.cs, CodexAdapter.cs, CopilotAdapter.cs, CursorAdapter.cs, ProviderRegistry.cs
    Install/        Installer.cs, InstallPlan.cs, LockFile.cs, ConfigMergers/ (McpMerger, HookMerger per provider)
    External/       ExternalResolver.cs, SourceManager.cs, GitClient.cs
    Primitives/     SemVersion.cs, Sha256.cs, Ids.cs, Result.cs
    Infra/          ContentHash.cs, JsonStore.cs, ProcessRunner.cs
  AgentPack.Cli/
    Commands/       AddCommand.cs, NewCommand.cs, ListCommand.cs, UpgradeCommand.cs, ... (one class per command)
    Ui/             Output.cs (tables/colors), Prompts.cs (interactive selection), Errors.cs
    Program.cs      (composition only, ~40 lines)
```

One adapter per file; `ProviderConfigWriters.cs` (572 lines) splits into per-provider mergers with a shared interface.

### 1.3 Adopt System.CommandLine

Replace the hand-rolled `Option()/Has()/Positional()` parsing. Gets for free: typed options, required/optional enforcement, `--help` per command, tab completion (bash/zsh/PowerShell), "did you mean" suggestions on typos, and consistent error text. The current parser has real traps (e.g. `--group backend review` silently drops `review`; `Positional()` guesses whether the next token is a value).

### 1.4 Error model

- `CliException(message, hint, exitCode)` for expected user errors — rendered as `error: <message>` + `hint: <try this>`, no stack trace.
- Unexpected exceptions: full stack to stderr only with `--verbose`, plus "this is a bug, report at <repo>" line, distinct exit code (70).
- Exit codes documented: 0 ok, 1 user error, 2 validation failed, 3 drift/conflict, 70 internal.
- Never silently `continue`: today `Apply()` skips items with local changes without printing anything — every skipped item must say why.

### 1.5 Known bugs to fix in this phase

- `ProcessRunner` reads stdout fully before stderr — deadlocks when a git command fills the stderr pipe buffer. Read both async.
- `ProcessRunner` has no timeout.
- `RewriteChecksum` edits YAML by regex — replaced entirely by generated `catalog.lock.yaml` (0.1).
- `SemVer.ParseCore` throws unhandled `FormatException` on malformed versions from the catalog.
- `catch (Exception)` at top level flattens everything to exit 1 with message only.

---

## Phase 2 — CLI UX (Claude Code-level polish)

### 2.1 Adopt Spectre.Console

Tables with borders/color, styled status glyphs, progress spinners for git operations, tree views for `status`. Works in Windows PowerShell, Windows Terminal, and macOS terminals; degrades cleanly when redirected (no ANSI when not a TTY).

Target look:

```
agentpack add

? Select assets to install (space to toggle, enter to confirm)
  Skills
  ◉ grill-me        1.0.0   Challenges a plan with critical review questions
  Hooks
  ◉ secret-scan     1.0.0   Blocks likely secrets before tool execution   [security: required]
  MCP
  ◯ github          1.0.0   GitHub MCP server (needs GITHUB_TOKEN)

Plan (project scope → claude, codex)
┌────────────┬───────┬──────────┬─────────┬────────────┬────────────────────────────┐
│ ID         │ Kind  │ Provider │ Version │ Action     │ Target                     │
├────────────┼───────┼──────────┼─────────┼────────────┼────────────────────────────┤
│ grill-me   │ skill │ claude   │ 1.0.0   │ install    │ .claude/skills/grill-me    │
│ secret-scan│ hook  │ claude   │ 1.0.0   │ merge into │ .claude/settings.json      │
└────────────┴───────┴──────────┴─────────┴────────────┴────────────────────────────┘
? Apply? (Y/n)
```

### 2.2 Interactive selection everywhere it matters

- `agentpack add` with no ids → multi-select checklist of all installable assets, grouped by kind — **including hooks and MCP**, answering "hooks and mcp get also a list to check what they want install or update".
- `agentpack upgrade` with no ids → checklist of outdated entries, preselected.
- Every apply shows the plan table + confirm prompt.
- Non-interactive mode: `--yes` flag and auto-detection of no-TTY/CI (`CI` env var) → current non-prompting behavior. Interactivity is additive, never blocks automation.

### 2.3 Local-modification protection (skills, hooks, MCP alike)

The lockfile checksum already detects drift. Change behavior from "silently skip" to ask:

```
⚠ secret-scan (claude) was modified locally after install.
? Overwrite with catalog version 1.1.0? [o]verwrite / [k]eep local / [d]iff / [a]bort
```

- `--force` = overwrite all, `--keep-local` = keep all (CI-friendly).
- `[d]iff` shows a unified diff (content trees) or the JSON merge preview (hooks/MCP in settings.json / mcp.json / config.toml) **before** writing.
- Same flow on `upgrade`. Backup to `.agentpack/backups/` stays as the safety net.

### 2.4 Helpful failure output

- Unknown command/asset/profile → Levenshtein suggestion: `Unknown command 'isntall'. Did you mean 'install'?` plus 5-line usage excerpt.
- Unknown asset id → nearest ids + `run 'agentpack list'`.
- Every error carries a one-line hint (what to run next), consistent with Claude Code's style.

---

## Phase 3 — Provider correctness (Claude, Codex, Copilot, Cursor)

The adapter shape is right; the mapping data must be verified against real product behavior, per provider, per kind. Current mappings contain guesses (e.g. `.codex/hooks.json`, `.github/skills/`, `.cursor/instructions/`) that may target files no product reads — installs that "succeed" but do nothing are worse than an explicit error.

1. **Audit matrix**: for each provider × kind, document (a) supported? (b) project-scope path, (c) user-scope path, (d) file format, with a link to official docs. Store as `docs/provider-mapping.md` and keep it the single source the adapters are reviewed against.
2. **Unsupported = explicit**: if a provider has no concept for a kind, the adapter reports `Unsupported` and the plan table shows `skipped (copilot has no hooks)` — never a made-up path.
3. **Golden-file tests per adapter**: install each kind into a temp dir, assert exact file layout and exact merged JSON/TOML output (`settings.json`, `.mcp.json`, `config.toml`, `mcp.json`). These are the tests that catch provider format drift.
4. **Merge safety**: merging into user-owned files (`.claude/settings.json`, `.codex/config.toml`) must preserve unknown keys and formatting where feasible, and must never drop user entries. Property test: merge(install) then remove == original.
5. Provider format changes over time → mapping data versioned with the tool; `agentpack doctor` warns when it detects provider config in unexpected locations.

## Phase 4 — External assets (GitHub installs with versions)

- Resolve `url@ref`: sparse/shallow clone at the pinned SHA into `~/.agentpack/cache/external/<sha>/`, extract the subfolder, hash it, record hash in the lockfile. Verify hash on every subsequent install (tamper detection).
- Tags: allow `@v1.2.0`; CI resolves tag → SHA at merge time and pins in `catalog.lock.yaml` (tags can move; SHAs cannot).
- `agentpack outdated --external`: check upstream for newer tags/commits on the same path, report only — bumping the ref is a PR to the catalog (deliberate: upgrades of third-party code get reviewed).
- Record `license` at import; validator warns when missing on external assets.
- Private repos: reuse the user's git credential helper (already implied by shelling out to git) — document it.
- Security note: hooks execute code. Keep `status: blocked` enforcement, keep SHA pinning, and require CODEOWNERS security review on `assets/hooks/**` and any external source change.

## Phase 5 — Engineering hygiene, CI/CD, release

- **CI (tool repo)**: build + test on ubuntu/windows/macos matrix (path separators, CRLF, no-exec-bit are real risks for this tool), `dotnet format --verify-no-changes`, analyzers on, `TreatWarningsAsErrors`.
- **Tests to add**: adapter golden files (Phase 3), merge round-trip property tests, SemVersion property tests, CLI end-to-end tests invoking the built binary and asserting output/exit codes, drift-protection scenario tests (modify installed file → expect prompt/skip), lockfile forward-compat test (unknown fields survive).
- **Release**: pack as .NET global tool to the org's internal NuGet feed; versioning via MinVer (git-tag driven); signed packages if org requires. `agentpack --version` + a lightweight self-update check ("v0.3.0 available: dotnet tool update -g AgentPack").
- **Catalog repo CI**: `agentpack catalog validate` + checksum/lock generation + external ref verification on every PR; branch protection + CODEOWNERS.
- **Docs**: three personas — consumer (install/upgrade), author (new asset → PR), admin (catalog, profiles, governance). Current docs/ folder is close; restructure around personas.
- Delete `.ProviderAdapters.cs.swp` from the repo; add `.gitignore` (bin/obj/.DS_Store); repo currently isn't a git repo — init it.

---

## Sequencing and effort (rough)

| Phase | Content | Effort |
|---|---|---|
| 0 | Schema simplification, drop bundles, CLI surface cleanup | 2–3 days |
| 1 | Type-safe domain, project split, System.CommandLine, error model, bug fixes | 4–6 days |
| 2 | Spectre.Console, interactive select, drift prompts, typo suggestions | 3–4 days |
| 3 | Provider audit + golden tests | 3–4 days (audit is the long pole) |
| 4 | External hardening | 2–3 days |
| 5 | CI/CD, release, docs | 2–3 days |

Phases 0–1 before anything else: every later phase writes code against the typed model, and schema changes get more expensive once real catalogs exist.
