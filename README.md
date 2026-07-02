# AgentPack

`agentpack` is a .NET global tool for managing organization-approved AI assets across **Claude Code, Codex, GitHub Copilot, Cursor**, and future providers.

One catalog repo is the trusted source of truth. Developers install from it; contributions go through pull requests; CI validates everything.

It manages:

- **skills** — agent skills (SKILL.md folders)
- **hooks** — pre/post tool-use hooks (Claude Code, Cursor)
- **mcp** — MCP server configs, merged into each provider's config file
- **instructions** — CLAUDE.md / AGENTS.md / Copilot instructions
- **rules** — Cursor rules (.mdc)
- **prompts** — reusable prompts / commands
- **profiles** — what a team installs, in one command

## Install

```bash
dotnet tool install -g AgentPack --add-source <your-org-feed>
agentpack --help
```

## Quickstart (consumer)

```bash
# In a repo that uses Claude Code / Cursor / Copilot / Codex:
agentpack add                 # interactive checklist of everything installable
agentpack add grill-me        # or install by id
agentpack add --group backend # or install a whole group
agentpack profile apply backend   # or apply your team's profile

agentpack status              # what is installed, and what has updates
agentpack upgrade             # upgrade (interactive checklist, asks before overwriting local edits)
agentpack outdated            # report only
agentpack remove grill-me
```

Providers are auto-detected from the repo (`.claude/`, `.cursor/`, `AGENTS.md`, ...) or forced with `--claude --codex --copilot --cursor`. Scope defaults to the project when inside a git repo; `--user` installs into your home directory instead.

If an installed asset was modified locally, `add`/`upgrade` ask what to do — overwrite, keep, or show a diff — instead of silently clobbering it. `--force` / `--keep-local` / `--yes` make it scriptable. Every overwrite is backed up under `.agentpack/backups/`.

## Quickstart (author)

Everything derivable is derived: `id` and `kind` come from the folder path, content lives in `content/`, checksums live in the generated `catalog.lock.yaml` — never written by hand.

```bash
agentpack new skills grill-me --group review
# edit assets/skills/grill-me/content/SKILL.md
git checkout -b add-grill-me && git add assets && git commit && gh pr create
```

A complete manifest is ~6 lines:

```yaml
# assets/skills/grill-me/agentpack.yaml
name: Grill Me
version: 1.0.0
description: Challenges a plan with critical review questions.
groups: [engineering, review]
# providers omitted = available for all providers
```

### External assets (from GitHub etc.)

```bash
agentpack import https://github.com/anthropics/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d
```

One line in the manifest, pinned to the exact commit you reviewed:

```yaml
source: https://github.com/anthropics/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d
```

AgentPack never follows upstream branches. Bumping the ref is a PR — a human re-reviews the upstream change. `agentpack catalog verify-external` (CI) fetches every pinned ref and verifies checksums.

## Catalog repo layout

```text
catalog.yaml          # groups + profiles
catalog.lock.yaml     # generated checksums — 'agentpack catalog lock', committed by CI
assets/
  skills/grill-me/
    agentpack.yaml
    content/SKILL.md
  hooks/secret-scan/
    agentpack.yaml
    content/hook.sh
  mcp/github/
    agentpack.yaml    # mcp: section, no content needed
```

Catalog CI on every PR:

```yaml
- run: agentpack catalog validate          # manifests, references, checksums
- run: agentpack catalog lock --check      # lock file up to date?
- run: agentpack catalog verify-external   # pinned refs still resolve + hash-match
```

Governance: branch protection + CODEOWNERS on `assets/**` (e.g. security team owns `assets/hooks/**` and all external source changes).

## Provider support

| Kind | Claude | Codex | Copilot | Cursor |
|---|---|---|---|---|
| skills | ✓ | ✓ | ✓ | ✓ |
| hooks | ✓ | ✓ | ✓ | ✓ |
| mcp | ✓ | ✓ | ✓ | ✓ |
| instructions | ✓ | ✓ | ✓ | ✓ |
| rules | — | — | — | ✓ |
| prompts | ✓ | ✓ | ✓ | ✓ |

Skills, hooks, and MCP work on all four providers — each in the product's native config format (e.g. hooks: `.claude/settings.json`, `.codex/hooks.json`, `.github/hooks/<id>.json`, `.cursor/hooks.json`). "—" means the product has no such concept; agentpack reports an explicit skip with the reason instead of writing files nothing reads. Exact paths and formats: [docs/provider-mapping.md](docs/provider-mapping.md).

## Commands

| Command | What it does |
|---|---|
| `add [kind] [id...]` | Install (interactive checklist when no args) |
| `plan ...` | Dry-run of add |
| `upgrade` / `outdated` | Update installed assets / report updates |
| `remove <id...>` | Uninstall (backups kept) |
| `status` / `diff <id>` | Installed state / local-modification check |
| `pin` / `unpin <id>` | Hold an asset at its installed version |
| `new <kind> <id>` | Scaffold a local asset |
| `import <url@ref>` | Scaffold an external asset |
| `profile list/plan/apply` | Team profiles |
| `catalog validate/lock/verify-external` | Catalog CI commands |
| `source add/list/sync` | Use a remote catalog repo |
| `doctor` | Environment diagnosis |

Exit codes: `0` ok · `1` user error · `2` validation failed · `3` drift/conflict · `70` internal error.

## Development

```bash
dotnet build     # warnings are errors
dotnet test      # 104 tests incl. provider golden files + CLI end-to-end
```

More docs: [catalog authoring](docs/catalog-authoring.md) · [external assets](docs/external-assets.md) · [groups & profiles](docs/groups-bundles-profiles.md) · [governance](docs/governance.md) · [breaking changes](docs/breaking-changes.md)
