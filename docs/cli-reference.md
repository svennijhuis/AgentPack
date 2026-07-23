# AgentPack CLI Reference

AgentPack uses one active catalog. Assets are installed either for the current user or into the current repository. New assets enter the catalog through pull requests created by `submit`.

## Basics

```bash
agentpack                  # onboarding screen with the common commands
agentpack --help           # every command
agentpack help <command>   # one command in detail, including nested ones
agentpack --version        # tool version
```

## Common options

- `--user`: install or manage assets for the current user on this device.
- `--project`: install or manage assets in the current repository.
- `--claude`, `--codex`, `--copilot`, `--cursor`: explicitly select providers.
- `-p|--provider <name>`: repeatable provider selection.
- `-g|--group <name>`: repeatable, comma-separated group selection.
- `-y|--yes`: skip confirmation prompts.
- `--force`: replace a locally modified managed install after backing it up.
- `--keep-local`: preserve a locally modified install.

Without a scope flag, AgentPack chooses project scope inside a Git repository and user scope elsewhere. Without provider flags, it detects providers from the directory the install writes to — your home directory for `--user`, the repository for `--project` — and falls back to the other one before reporting that it found none.

## Discover

### `agentpack search <query> [options]`

Search IDs, names, descriptions, kinds, groups, and external repository metadata.

```bash
agentpack search "writing"
agentpack search review --kind skills --group backend --provider codex
```

Filter with `-k|--kind`, `-g|--group`, and `-p|--provider`.

### `agentpack list [kind] [options]`

Browse approved catalog assets. Filter with `--group` or `--provider`.

```bash
agentpack list
agentpack list skills --group review
```

### `agentpack groups`

List the catalog's discovery groups and lifecycle status.

## Install and manage

### `agentpack install [kind] [id...] [options]`

Install approved assets. With no target in an interactive terminal, opens a picker.

```bash
agentpack install grill-me --user
agentpack install secret-scan --project
agentpack install skills --codex --project
agentpack install --group security --project
```

Pass IDs, a kind, or `-g|--group <name>` to install everything in a group.

Add `--dry-run` to show the catalog, scope, providers, actions, and exact destinations without writing files:

```bash
agentpack install grill-me --codex --user --dry-run
```

Remote catalogs refresh before an install. If the network is unavailable and a cache exists, AgentPack warns and uses that cache.

### `agentpack status [--user|--project]`

Show installed versions, available catalog versions, pins, and state.

### `agentpack update [kind] [id...] [options]`

Refresh the catalog and update installed assets. With no target in an interactive terminal, opens a picker with outdated assets selected.

### `agentpack outdated [kind] [id...] [options]`

Report updates without applying them.

### `agentpack remove <id...> [options]`

Remove managed files and managed fragments from shared provider configuration.

### `agentpack diff <id> [--user|--project]`

Report whether an installed asset is clean, missing, or locally modified.

### `agentpack pin <id> [--user|--project]`

Exclude an installed asset from updates.

### `agentpack unpin <id> [--user|--project]`

Allow a pinned asset to update again.

## Submit to the catalog

### `agentpack submit <kind> <path-or-url-or-id> [options]`

Propose a local or external asset through a catalog pull request.

```bash
agentpack submit skill ./my-skill
agentpack submit hook ./secret-scan --command scripts/check.sh --group security
agentpack submit mcp github --command github-mcp-server --env GITHUB_TOKEN
agentpack submit skill https://github.com/acme/skills/tree/main/skills/review
agentpack submit skill ./my-skill --update
```

Options:

- `--id <id>`: override the ID derived from the path or URL.
- `--name <name>` and `--description <text>`: set display metadata.
- `--update`: propose a new revision of an asset already in the catalog instead of adding one.
- `--version <semver>`: defaults to `1.0.0` for a new asset, and to the next patch of the catalog version when updating. It must always be higher than the version already in the catalog.
- `--ref <commit-or-tag>`: select an immutable external revision; when omitted, AgentPack pins the latest current commit.
- `--license <spdx>`: record an external asset's license when known.
- `-g|--group` and `-p|--provider`: set catalog metadata.
- `--command <value>`: hook entry file or stdio MCP executable.
- `--trigger`, `--tool`, `--timeout`: configure a hook; the default trigger is `preToolUse` and timeout is 30 seconds.
- `--url`, `--transport`: configure an HTTP/SSE MCP endpoint; transport is normally inferred.
- `--arg <value>`: repeatable stdio MCP argument.
- `--env <name>`: repeatable required environment-variable name for an MCP process.
- `--header-env <header=env>`: repeatable environment-backed remote MCP header.
- `--yes`: accept the preview without an interactive publishing confirmation.
- `--draft`: open a draft pull request.
- `--prepare-only`: create and commit the proposal branch locally without pushing.

Skills can be folders and must contain `SKILL.md`. Hooks can be one script or a folder; ambiguous folders require `--command`. MCP servers use typed command/URL options rather than arbitrary folder copying. Tools and templates are not accepted until a supported provider can install them safely.

Before cloning or publishing, local submissions display the exact included files. Common VCS/build/cache paths are ignored; symlinks, credential-like files, private keys, and oversized folders are rejected. Automatic submission requires an authenticated GitHub CLI. Contributors with read-only catalog access automatically submit through their fork. The command never commits or pushes to the catalog's default branch.

## Catalog

### `agentpack catalog status`

Show the active catalog repository, branch, revision, last refresh, and local cache.

Inside a catalog checkout — any directory with a `catalog.yaml` — that checkout takes precedence over the configured catalog, and `catalog status` reports its location and revision instead.

### `agentpack catalog sync`

Force an immediate catalog refresh. Install and update already refresh automatically.

### `agentpack catalog use <git-url> [options]`

Replace the built-in official catalog with another approved catalog and sync it immediately.

```bash
agentpack catalog use https://github.com/acme/ai-catalog.git --name company
```

Use `--branch <branch>` when the catalog does not use `main`.

### Catalog CI and recovery commands

```bash
agentpack catalog validate
agentpack catalog validate --no-checksums   # structure only, skip content hashing
agentpack catalog lock
agentpack catalog lock --check              # fail if the lockfile is out of date
agentpack catalog lock --no-fetch           # skip external checksums (no network)
agentpack catalog verify-external
```

Contributors do not normally run these commands; `submit` performs them in the background. They remain public for CI and catalog incident recovery.

## Environment variables

| Variable | Effect |
|---|---|
| `AGENTPACK_CATALOG_URL` | Organization catalog, used when no catalog was selected with `catalog use` |
| `AGENTPACK_CATALOG_BRANCH` | Branch for `AGENTPACK_CATALOG_URL`. Default: `main` |
| `AGENTPACK_DEFAULT_CATALOG_URL` | Replaces the built-in official catalog URL |
| `AGENTPACK_DISABLE_DEFAULT_CATALOG` | `1` removes the built-in catalog, for hermetic or offline environments |
| `AGENTPACK_HOME` | Relocates AgentPack's own state (config, caches, lockfile). Never moves `.claude/`, `.codex/`, … |
| `AGENTPACK_DEBUG` | `1` includes a stack trace for internal failures |

Catalog precedence: an explicit path, then a `catalog.yaml` in the working directory, then the catalog selected with `catalog use`, then `AGENTPACK_CATALOG_URL`, then the built-in official catalog.

## Profiles

```bash
agentpack profile list
agentpack profile plan backend --project
agentpack profile apply backend --project --yes
```

Profiles select a reviewed set of catalog assets and providers.

## Diagnostics

### `agentpack doctor`

Show the CLI version, working directory, detected providers, active catalog, catalog selection, and default install scope. It does not download or modify the catalog.

## Exit codes

| Code | Meaning |
|---:|---|
| `0` | Success |
| `1` | Invalid input or missing prerequisite |
| `2` | Catalog validation failure |
| `3` | Drift or conflict |
| `70` | Unexpected internal error |

Set `AGENTPACK_DEBUG=1` to include a stack trace for internal failures.
