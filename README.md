# AgentPack

**Share approved AI assets across Claude Code, Codex, GitHub Copilot, and Cursor.**

AgentPack installs skills, hooks, MCP servers, instructions, rules, prompts, and custom agents from a reviewed catalog into each provider's native format. It works for one personal repository, a service team's local conventions, or a company-wide platform catalog.

## Install

```bash
dotnet tool install -g AgentPack
agentpack --version
```

The global tool requires the .NET SDK. Self-contained binaries that need no .NET runtime are also published on the [GitHub Releases page](https://github.com/svennijhuis/AgentPack/releases) for Windows, Linux, and macOS on x64 and ARM64.

## Choose your quickstart

### Use an existing team or company catalog

```bash
agentpack source add org https://github.com/your-org/ai-catalog.git
agentpack find "service setup"
agentpack add service-setup
```

The catalog syncs automatically on first use. `agentpack add` with no asset id opens an interactive picker.

### Start in a personal project or service repository

```bash
cd my-project
agentpack init --overlay
agentpack new skills service-setup --overlay
agentpack add service-setup --codex --project --yes
```

This keeps project-owned assets under `.agentpack/`, ready to commit and review in the same repository. If a central catalog is configured later, the project catalog becomes an overlay on top of it.

### Create a shared catalog

```bash
mkdir ai-catalog && cd ai-catalog
git init
agentpack init
agentpack new skills service-setup
agentpack catalog lock
agentpack catalog validate
```

Commit the generated `catalog.yaml`, `catalog.lock.yaml`, and `assets/` directory, then open a PR. Use `agentpack import <url>@<commit-sha>` for reviewed external assets.

## Command cheat sheet

| Goal | Command |
|---|---|
| See guided next steps | `agentpack` |
| Initialize a shared or project catalog | `agentpack init [--overlay]` |
| Browse or search approved assets | `agentpack list` / `agentpack find <query>` |
| Install or preview assets | `agentpack add [kind] [id...]` / `agentpack plan ...` |
| Check or update installs | `agentpack status` / `agentpack outdated` / `agentpack upgrade` |
| Remove, compare, or pin | `agentpack remove` / `diff` / `pin` / `unpin` |
| Create a local or external asset | `agentpack new` / `agentpack import` |
| Apply a team standard | `agentpack profile list\|plan\|apply` |
| Validate catalog PRs | `agentpack catalog validate\|lock\|verify-external` |
| Manage shared catalog repositories | `agentpack source add\|list\|sync` |
| Diagnose configuration | `agentpack doctor` |

Run `agentpack help <command>` for command-specific help. The [complete CLI reference](docs/cli-reference.md) documents every argument, option, alias, default, and example.

## What it manages

Everything works on all four providers unless the product itself has no corresponding feature:

| Kind | What it is | Claude | Codex | Copilot | Cursor |
|---|---|:-:|:-:|:-:|:-:|
| **skills** | `SKILL.md` instruction packages | ✓ | ✓ | ✓ | ✓ |
| **hooks** | pre/post tool-use and lifecycle scripts | ✓ | ✓ | ✓ | ✓ |
| **mcp** | MCP server configurations | ✓ | ✓ | ✓ | ✓ |
| **instructions** | repository or user instructions | ✓ | ✓ | ✓ | ✓ |
| **prompts** | reusable prompts and slash commands | ✓ | ✓ | ✓ | ✓ |
| **agents** | custom agents and subagents | ✓ | ✓ | ✓ | ✓ |
| **rules** | scoped rule files | ✓ | — | — | ✓ |
| **profiles** | a team's approved toolkit | ✓ | ✓ | ✓ | ✓ |

“—” means the provider has no such feature. AgentPack reports the skip instead of installing a file the provider will not read. The audited paths and translations are documented in [provider-mapping.md](docs/provider-mapping.md).

## Personal, team, and company sharing

- **Personal project:** `.agentpack/catalog.yaml` and `.agentpack/assets/` can be the complete local catalog.
- **Service or team repository:** the same `.agentpack/` structure overlays a configured company catalog. Put service setup, review conventions, or local prompts here and approve them in that repository's PR workflow.
- **Company catalog:** reusable assets, security hooks, MCP definitions, groups, and profiles live in a dedicated catalog repository with central review.

Broadly reusable assets belong in the central catalog. Service-specific assets belong in the owning repository. Both remain versioned, reviewable, and installable through the same CLI.

## Everyday workflows

```bash
# Browse and install
agentpack list skills --group backend
agentpack find "typescript service" --kind skills
agentpack add
agentpack add skills --claude
agentpack add grill-me secret-scan

# Team profiles
agentpack profile plan backend
agentpack profile apply backend

# Updates and local changes
agentpack status
agentpack outdated
agentpack upgrade
agentpack diff grill-me
agentpack pin grill-me
agentpack remove grill-me
```

Providers are detected from the repository. Override detection with `--claude`, `--codex`, `--copilot`, `--cursor`, or `--provider <name>`. Installs default to project scope inside a git repository and user scope otherwise; use `--project` or `--user` to be explicit.

## Safety and governance

- **Local edits are protected.** AgentPack prompts before overwriting, or uses `--force` / `--keep-local` in non-interactive workflows.
- **Shared configuration is merged.** Existing MCP and hook entries are preserved; conflicting entries fail instead of being silently replaced.
- **Changes are recoverable.** Replaced files are backed up under `.agentpack/backups/`.
- **External content is pinned.** Moving branches are rejected; content hashes are recorded in `catalog.lock.yaml`.
- **Catalog owners have a kill switch.** Assets marked `blocked` cannot be installed.
- **Secrets stay out of git.** MCP environment variables are declared by name and translated into each provider's reference syntax.
- **CI is deterministic.** `--yes`, explicit filters, stable exit codes, and catalog validation support scripted use.

## Authoring and approval

```bash
# Shared catalog asset
agentpack new skills grill-me --group review

# Project-owned team asset
agentpack new skills service-setup --overlay

# Reviewed third-party asset
agentpack import https://github.com/example-org/agent-assets/tree/main/skills/code-review@<commit-sha>

# PR checks
agentpack catalog validate
agentpack catalog lock --check
agentpack catalog verify-external
```

An asset manifest stays small because its id, kind, content path, and checksums are derived:

```yaml
name: Grill Me
version: 1.0.0
description: Challenges a plan with critical review questions.
groups: [engineering, review]
# providers omitted = available to all supported providers
```

External assets record their repository URL and pinned revision, which identify the upstream project and exact reviewed content. A license can be recorded with optional `--license <license>`. When someone installs the asset, `agentpack add` shows the repository in its plan and confirmation.

Profiles combine groups and explicit assets into a one-command team setup. See [catalog authoring](docs/catalog-authoring.md), [team overlays](docs/team-overlays.md), [groups and profiles](docs/groups-bundles-profiles.md), and [governance](docs/governance.md).

## Installation alternatives

Update the global tool with:

```bash
dotnet tool update -g AgentPack
```

For a standalone release, download the archive for your platform, extract `agentpack`, and put it on `PATH`. Organizations can set `AGENTPACK_CATALOG_URL` and optional `AGENTPACK_CATALOG_BRANCH` during machine setup so developers do not need to register the company catalog manually.

To develop the CLI from this repository:

```bash
dotnet run --project src/AgentPack.Cli -- --help
dotnet run --project src/AgentPack.Cli -- list

dotnet pack src/AgentPack.Cli -c Release -o ./artifacts/packages -p:Version=0.0.1-dev.1
dotnet tool install -g AgentPack --add-source ./artifacts/packages --version 0.0.1-dev.1
```

## Development

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
```

Architecture and behavior: [how it works](docs/how-it-works.md) · [CLI design](docs/cli-design.md) · [provider mapping](docs/provider-mapping.md) · [external assets](docs/external-assets.md) · [breaking changes](docs/breaking-changes.md)

Exit codes: `0` success · `1` user error · `2` validation failure · `3` drift/conflict · `70` internal error.

## License

AgentPack is open source under the [MIT License](LICENSE). You may use, copy, modify, merge, publish, distribute, sublicense, and sell the software. If you copy AgentPack or a substantial portion of its code, keep the copyright and MIT license notice with it. Contributions and improvements are welcome.
