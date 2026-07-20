# AgentPack CLI Reference

Use `agentpack --help` for the complete generated command list, `agentpack help <command>` for one command, or run `agentpack` with no arguments for a task-oriented introduction.

## Conventions and defaults

- Provider detection uses repository markers for Claude Code, Codex, GitHub Copilot, and Cursor. Override it with `--claude`, `--codex`, `--copilot`, `--cursor`, or repeatable `-p|--provider <name>`.
- Scope defaults to project inside a git repository and user outside one. Override it with `--project` or `--user`.
- `--yes` disables confirmations for scripts and CI. `--force` overwrites local changes; `--keep-local` preserves them. `--force` and `--keep-local` cannot be combined.
- Repeatable group and provider options also accept comma-separated values.
- Familiar aliases are `install` for `add`, `ls` for `list`, `search` for `find`, `uninstall` for `remove`, and `update` for `upgrade`.

## Getting started and discovery

### `agentpack init [--overlay]`

Initialize a catalog without overwriting an existing one.

- Default: create `catalog.yaml` for a dedicated shared catalog.
- `--overlay`: create `.agentpack/catalog.yaml` for a personal project or service repository.

```bash
agentpack init
agentpack init --overlay
```

### `agentpack list [kind] [options]`

List approved assets in the effective catalog. Alias: `agentpack ls`.

- `[kind]`: `skills`, `hooks`, `mcp`, `tools`, `instructions`, `rules`, `prompts`, `templates`, `agents`, or `all`.
- `-g|--group <group>`: filter by group.
- `-p|--provider <provider>`: filter by provider.

```bash
agentpack list
agentpack list skills --group backend --provider codex
```

### `agentpack find <query> [options]`

Search only the approved effective catalog. Aliases: `agentpack search`.

- `<query>`: one or more words; every word must match the combined id, name, description, kind, or groups.
- `-k|--kind <kind>`: filter by kind.
- `-g|--group <group>`: filter by group.
- `-p|--provider <provider>`: filter by provider.

```bash
agentpack find "service setup"
agentpack search review --kind skills --group backend --provider codex
```

### `agentpack groups`

List catalog group ids, names, status, replacements, and removal dates.

```bash
agentpack groups
```

## Installing and maintaining assets

### `agentpack add [kind] [id...] [options]`

Install assets. Alias: `agentpack install`. With no targets in an interactive terminal, opens the approved-asset picker. A kind or group filters the picker; explicit ids install directly.

- `-g|--group <group>`: select a group.
- Provider and scope options follow the global conventions above.
- `-y|--yes`, `--force`, and `--keep-local` control non-interactive and drift behavior.

```bash
agentpack add
agentpack add skills --claude
agentpack install grill-me secret-scan --project --yes
```

### `agentpack plan [kind] [id...] [options]`

Use the same selection and targeting options as `add`, but show the install plan without changing files.

```bash
agentpack plan skills --codex --project
agentpack plan --group security --claude
```

### `agentpack status [--user|--project]`

Show installed versions, latest catalog versions, pins, and update state for one scope.

```bash
agentpack status --project
agentpack status --user
```

### `agentpack outdated [kind] [id...] [options]`

Report available upgrades without applying them. Accepts kind/id, provider, and scope filters.

```bash
agentpack outdated
agentpack outdated skills --codex --project
```

### `agentpack upgrade [kind] [id...] [options]`

Upgrade installed assets to catalog versions. Alias: `agentpack update`. With no targets in an interactive terminal, opens a picker with outdated assets preselected.

```bash
agentpack upgrade
agentpack update grill-me --project --yes
```

### `agentpack remove <id...> [options]`

Remove installed assets and their managed shared-config fragments. Alias: `agentpack uninstall`.

- Targets may start with a kind or `all`.
- Provider and scope options limit which installs are removed.

```bash
agentpack remove grill-me --project
agentpack uninstall skills old-skill --codex
agentpack remove all --user
```

### `agentpack diff <id> [--user|--project]`

Compare an installed asset with AgentPack's recorded fragment or checksum and report clean, missing, or locally modified state.

```bash
agentpack diff grill-me --project
```

### `agentpack pin <id> [--user|--project]`

Keep an installed asset at its current version during upgrades.

```bash
agentpack pin grill-me --project
```

### `agentpack unpin <id> [--user|--project]`

Allow a pinned asset to be upgraded again.

```bash
agentpack unpin grill-me --project
```

## Authoring assets

### `agentpack new <kind> <id> [options]`

Scaffold `agentpack.yaml` and kind-appropriate content.

- `--overlay`: write under `.agentpack/assets/` and create the minimal overlay catalog if needed.
- `--name <name>` and `--description <text>`: set display metadata.
- `-g|--group <group>` and `-p|--provider <provider>`: set catalog metadata.
- `--owner <team>`: record an optional owner.
- `--force`: overwrite an existing manifest.

```bash
agentpack new skills service-setup --group backend
agentpack new prompts release-check --overlay
```

### `agentpack import <url[@ref]> [options]`

Scaffold a catalog manifest for reviewed external content. A full commit SHA or immutable tag is required; moving branches are rejected.

- `--ref <ref>`: provide the reviewed ref separately.
- `--kind <kind>`: defaults to `skills`.
- `--id <id>`: defaults to the final URL path segment.
- `--license <license>`: record the upstream license.
- Group, provider, and force options match `new`.

```bash
agentpack import https://github.com/anthropics/skills/tree/main/skills/pdf@<commit-sha> --license MIT
```

## Profiles

### `agentpack profile list`

List defined team profiles.

```bash
agentpack profile list
```

### `agentpack profile plan <id> [options]`

Preview everything selected by a profile. Accepts provider and scope options.

```bash
agentpack profile plan backend --project
```

### `agentpack profile apply <id> [options]`

Install everything selected by a profile. Accepts provider, scope, confirmation, and drift options.

```bash
agentpack profile apply backend --project --yes
```

## Catalog maintenance

### `agentpack catalog validate [--no-checksums]`

Validate manifests, references, policy, and—unless disabled—content checksums.

```bash
agentpack catalog validate
agentpack catalog validate --no-checksums
```

### `agentpack catalog lock [--check] [--no-fetch]`

Generate `catalog.lock.yaml`. `--check` compares without writing and is intended for CI; `--no-fetch` skips external fetches.

```bash
agentpack catalog lock
agentpack catalog lock --check
```

### `agentpack catalog verify-external`

Fetch every external asset at its pinned ref and verify it against the catalog checksum.

```bash
agentpack catalog verify-external
```

## Catalog sources

### `agentpack source add <name> <git-url> [--branch <branch>]`

Register a shared catalog repository. The branch defaults to `main`; first use clones it automatically.

```bash
agentpack source add org https://github.com/acme/ai-catalog.git
agentpack source add platform ssh://git/acme/catalog.git --branch stable
```

### `agentpack source list`

List registered source names, branches, and URLs.

```bash
agentpack source list
```

### `agentpack source sync`

Clone or fast-forward every registered source immediately.

```bash
agentpack source sync
```

## Diagnostics

### `agentpack doctor`

Show the CLI version, working directory, git status, detected providers, catalog mode and location, project overlay, registered source count, and default install scope. It does not clone or modify catalog state.

```bash
agentpack doctor
```

## General options and exit codes

```text
-h, --help       generated help
-v, --version    installed AgentPack version
```

Exit codes are `0` success, `1` user input error, `2` catalog validation failure, `3` drift or conflict, and `70` unexpected internal error. Set `AGENTPACK_DEBUG=1` to include a stack trace for internal failures.
