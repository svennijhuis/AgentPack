# AgentPack

The one-command installer for the AI coding catalog. Install skills, hooks, MCP servers, prompts, agents, instructions, and rules into **Claude Code**, **Codex**, **GitHub Copilot**, and **Cursor** — each in its native format, all from one reviewed catalog.

```bash
dotnet tool install -g AgentPack   # install the CLI once
agentpack list                     # browse the built-in catalog
agentpack install                  # pick assets from a list and install them
```

There is nothing to configure first. AgentPack ships a built-in, reviewed catalog, so `agentpack list` works immediately.

## What AgentPack installs

The catalog holds seven kinds of asset. AgentPack translates each into the native format of every provider that supports it.

| Kind | What it is |
|---|---|
| **Skills** | Reusable instruction sets an agent loads on demand — a `SKILL.md` folder |
| **Hooks** | Scripts that run on agent events (before/after a tool, on session start) |
| **MCP servers** | Model Context Protocol servers that give an agent new tools and data |
| **Prompts** | Saved prompt templates and slash commands |
| **Agents** | Specialized sub-agent definitions |
| **Instructions** | The top-level project instructions file (`CLAUDE.md` / `AGENTS.md`) |
| **Rules** | Path-scoped coding rules (Cursor `.mdc`, Claude rules) |

## Commands

Every command, in one place.

| Command | What it does |
|---|---|
| [`agentpack list [kind]`](#agentpack-list) | Browse the catalog, optionally filtered by kind |
| [`agentpack search <query>`](#agentpack-search) | Search the catalog by id, name, description, kind, or group |
| [`agentpack install [ids]`](#agentpack-install) | Install assets — pass ids, a kind, or nothing to pick from a list |
| [`agentpack update [ids]`](#agentpack-update) | Update installed assets to the catalog version |
| [`agentpack outdated`](#agentpack-outdated) | Show installed assets with a newer catalog version |
| [`agentpack status`](#agentpack-status) | Show what is installed and its state |
| [`agentpack remove <ids>`](#agentpack-remove) | Remove installed assets |
| [`agentpack diff <id>`](#agentpack-diff) | Check an installed asset for local modifications |
| [`agentpack pin <id>` / `unpin <id>`](#agentpack-pin--agentpack-unpin) | Freeze or unfreeze an asset against updates |
| [`agentpack submit <kind> <src>`](#agentpack-submit) | Propose an asset to the catalog through a pull request |
| [`agentpack groups`](#agentpack-groups) | List catalog groups |
| [`agentpack profile <cmd>`](#agentpack-profile) | List, plan, and apply team profiles |
| [`agentpack catalog <cmd>`](#agentpack-catalog) | Select, inspect, sync, and validate the catalog |
| [`agentpack config`](#agentpack-config) | Show agentpack's paths and set where its state lives |
| [`agentpack doctor`](#agentpack-doctor) | Show environment, detected providers, and configuration |

> [!NOTE]
> Run `agentpack help <command>` for the full options of any command, or `agentpack --help` for the complete surface.

## Install an asset

```bash
agentpack install code-review          # scope and providers are detected
agentpack install code-review --user   # available to you across projects
agentpack install code-review --project
```

The asset always comes from the approved catalog. Scope only controls its destination.

### Scope

| Scope | Meaning |
|---|---|
| `--user` | Installed in your home directory, available across every project |
| `--project` | Installed in the current repository, ready to commit for the team |

If you pass neither, AgentPack uses `--project` inside a Git repository and `--user` everywhere else.

### Providers

| Flag | Target |
|---|---|
| `--claude` | Claude Code |
| `--codex` | Codex |
| `--copilot` | GitHub Copilot |
| `--cursor` | Cursor |
| `-p, --provider <name>` | Any of the above by name (repeatable or comma-separated) |

Providers are detected from the directory the install writes to — your home directory for `--user`, the repository for `--project`. The flags above override that detection and are only required when no provider is found in either place.

### Options

| Option | Effect |
|---|---|
| `--dry-run` | Show the install plan without changing any files |
| `-g, --group <name>` | Install everything in a group (repeatable or comma-separated) |
| `-y, --yes` | Skip confirmation prompts (for scripts and CI) |
| `--force` | Overwrite locally modified installs without asking |
| `--keep-local` | Keep locally modified installs without asking |

### Examples

```bash
# Preview an install without writing files
agentpack install code-review --codex --user --dry-run

# Install every skill for Claude into the current repo, no prompts
agentpack install skills --claude --project --yes

# Install a whole group across detected providers
agentpack install --group backend --user
```

## Pick assets from a list

Run `install` (or `update`) with no ids to open an interactive picker: tick the assets you want and press Enter. It installs anything new and updates anything already installed when the catalog has a newer version.

```bash
agentpack install          # pick from everything
agentpack install skills   # pick from one kind
```

Rows already installed are marked, and a newer catalog version shows as `(update available)`, so you always see whether Enter will install or update.

> [!NOTE]
> The picker only appears in an interactive terminal. In CI, name the ids or a kind and pass `--yes`.

## Command reference

### `agentpack list`

Browse the catalog. Compact by default — id, version, and only the columns that carry a non-default value.

```bash
agentpack list                 # everything, grouped by kind in the footer
agentpack list skills          # one kind
agentpack list hooks --wide    # add descriptions, groups, and providers
agentpack list --group backend --provider claude
```

| Option | Effect |
|---|---|
| `[kind]` | Filter by kind: `skills`, `hooks`, `mcp`, `instructions`, `rules`, `prompts`, `agents`, or `all` |
| `-g, --group <name>` | Filter by group |
| `-p, --provider <name>` | Filter by provider |
| `-w, --wide` | Show extra columns: description, groups, and providers |

### `agentpack search`

Search approved asset metadata.

```bash
agentpack search review
agentpack search typescript --kind skills
```

Accepts the same `--kind`, `--group`, `--provider`, and `--wide` filters as `list`.

### `agentpack install`

Install catalog assets into your user profile or the current project. See [Install an asset](#install-an-asset) and [Pick assets from a list](#pick-assets-from-a-list) above.

### `agentpack update`

Update installed assets to the catalog versions. With no ids in a terminal, it opens the picker with updatable assets pre-selected.

```bash
agentpack update            # update everything (pick in a terminal)
agentpack update --user
agentpack update code-review
```

### `agentpack outdated`

Show installed assets that have a newer version in the catalog, without changing anything.

```bash
agentpack outdated --project
```

### `agentpack status`

Show installed assets and whether each is up to date, has an update available, or was removed from the catalog.

```bash
agentpack status --project
```

### `agentpack remove`

Remove installed assets, including the entries AgentPack merged into shared provider configs. Backups land under `.agentpack/backups/` (`~/.agentpack/backups` for `--user`).

```bash
agentpack remove code-review --project
agentpack remove skills --user          # remove by kind
```

### `agentpack diff`

Compare an installed asset against its lockfile checksum to see whether it was modified locally.

```bash
agentpack diff code-review --project
```

### `agentpack pin` / `agentpack unpin`

Pin an installed asset so `update` skips it; unpin to allow updates again.

```bash
agentpack pin code-review --project
agentpack unpin code-review --project
```

### `agentpack submit`

Propose an asset to the catalog through a pull request. See [Submit an asset](#submit-an-asset) for the full workflow.

### `agentpack groups`

List catalog groups and their status.

```bash
agentpack groups
```

### `agentpack profile`

Team profiles bundle a set of assets so a whole team installs the same thing in one command.

```bash
agentpack profile list
agentpack profile plan backend     # dry-run
agentpack profile apply backend
```

### `agentpack catalog`

Select, inspect, and maintain the catalog.

```bash
agentpack catalog status                              # active catalog, revision, cache, last refresh
agentpack catalog sync                                # refresh now
agentpack catalog use <git-url> --name company        # select an organization catalog
agentpack catalog validate                            # CI: manifests, references, checksums
agentpack catalog lock                                # CI: write catalog.lock.yaml
agentpack catalog verify-external                     # CI: re-fetch and checksum external assets
```

### `agentpack config`

Show every path AgentPack uses and choose where its state lives.

```bash
agentpack config                                  # show all paths and where each comes from
agentpack config --set-home ~/dotfiles/agentpack  # relocate agentpack's state directory
agentpack config --reset-home                     # revert to the default (~/.agentpack)
```

The **home** holds AgentPack's own state — catalog cache, lockfiles, and `config.json`. It does **not** move where `--user` installs go: provider files (`.claude/`, `.codex/`, …) always live in your OS user profile. See [Configuration and paths](#configuration-and-paths).

### `agentpack doctor`

Show the AgentPack version, home, working directory, detected providers, the active catalog, and the default scope. Start here when something is not detected as expected.

```bash
agentpack doctor
```

## Supported assets and providers

| Kind | Claude | Codex | Copilot | Cursor |
|---|:-:|:-:|:-:|:-:|
| Skills | ✓ | ✓ | ✓ | ✓ |
| Hooks | ✓ | ✓ | ✓ | ✓ |
| MCP servers | ✓ | ✓ | ✓ | ✓ |
| Instructions | ✓ | ✓ | ✓ | ✓ |
| Prompts | ✓ | ✓ | ✓ | ✓ |
| Agents | ✓ | ✓ | ✓ | ✓ |
| Rules | ✓ | — | — | ✓ |

“—” means the provider has no matching feature; AgentPack reports the skip instead of inventing a format. See [provider mapping](docs/provider-mapping.md) for exact paths and translations.

## Submit an asset

`submit` proposes an asset for review. It never pushes directly to `main`.

```bash
# A local skill folder (needs a top-level SKILL.md)
agentpack submit skill ./my-skill

# A hook — one script, or a folder plus its entry command
agentpack submit hook ./check-secrets.sh --trigger preToolUse --tool Bash
agentpack submit hook ./secret-check --command scripts/check.sh

# An MCP server as typed config — only env-var names are recorded, never secrets
agentpack submit mcp github --command github-mcp-server --arg stdio --env GITHUB_TOKEN
agentpack submit mcp company-docs --url https://mcp.example.com --header-env Authorization=COMPANY_MCP_TOKEN

# An external asset by URL — the id and pinned commit are derived for you
agentpack submit skill https://github.com/vercel-labs/skills/tree/main/skills/some-skill
```

| Kind | Submit input |
|---|---|
| Skill | Folder with top-level `SKILL.md`; supporting files and folders are included |
| Hook | One script, or a folder plus one reviewed entry command |
| Instruction, rule, prompt, agent | One file — providers install these as a single native file |
| MCP server | Id plus typed `--command` or `--url`; no arbitrary folder |
| Tool, template | Not accepted yet; no provider-native destination exists |

Iterate on an asset you already contributed with `--update`, which bumps the patch version so existing installs pick it up on `agentpack update`:

```bash
agentpack submit skill ./my-skill --update
```

AgentPack previews every file that will be proposed, asks for confirmation, then clones the catalog, creates an `agentpack/submit/...` branch, pins external sources to a full commit SHA, records checksums in `catalog.lock.yaml`, validates the catalog, and opens a pull request with the [GitHub CLI](https://cli.github.com/). The asset becomes available only after its PR is approved and merged.

> [!NOTE]
> Automatic PRs need `gh` and `gh auth login`. To build the branch without pushing or needing `gh`, use `--prepare-only`. In CI, `--yes` accepts the file preview.

## Use another catalog

The official catalog works without setup. An organization can select its own once:

```bash
agentpack catalog use https://github.com/your-org/ai-catalog.git --name company
```

The command validates the repository by syncing it immediately. Managed machines can instead set `AGENTPACK_CATALOG_URL` (and optional `AGENTPACK_CATALOG_BRANCH`). One active catalog keeps the trust model simple: the catalog is always the source; `--user` and `--project` only choose where an approved asset lands.

## Configuration and paths

`agentpack config` shows every path and where it resolves from. There are two roots, and they are deliberately separate:

| Root | Holds | Change it with |
|---|---|---|
| **home** | AgentPack's own state: catalog cache, lockfiles, `config.json` | `agentpack config --set-home <path>` or `AGENTPACK_HOME` |
| **provider home** | Where `--user` installs land (`.claude/`, `.codex/`, …) | Always your OS user profile — not relocated by AgentPack |

Precedence for the home directory is: `AGENTPACK_HOME` (per-invocation override) → a persisted `config --set-home` choice → the default `~/.agentpack`. Relocating the home does not move existing state automatically; copy the old directory's contents over to carry your installs and cache across.

### Environment variables

| Variable | Effect |
|---|---|
| `AGENTPACK_HOME` | Overrides the home directory for a single invocation |
| `AGENTPACK_CATALOG_URL` | Selects an organization catalog without `catalog use` |
| `AGENTPACK_CATALOG_BRANCH` | Branch for `AGENTPACK_CATALOG_URL` (default `main`) |
| `AGENTPACK_DEBUG=1` | Print a stack trace on unexpected errors |
| `NO_COLOR` / `CI` | Disable color and interactive prompts |

## Included assets and attribution

The official catalog is seeded from permissively licensed upstream projects. External entries are pinned to a reviewed commit and checksummed; the repository and ref are the attribution, shown again at install time.

| Upstream | License | Kinds it supplies |
|---|---|---|
| [mattpocock/skills](https://github.com/mattpocock/skills) | MIT | skills, one git-safety hook |
| [obra/superpowers](https://github.com/obra/superpowers) | MIT | skills |
| [JuliusBrussee/caveman](https://github.com/JuliusBrussee/caveman) | MIT | skills |
| [vercel-labs/skills](https://github.com/vercel-labs/skills) | MIT | skills |
| [microsoft/azure-skills](https://github.com/microsoft/azure-skills) | MIT | skills |
| [shadcn-ui/ui](https://github.com/shadcn-ui/ui) | MIT | skills |
| [wshobson/agents](https://github.com/wshobson/agents) | MIT | agents |
| [wshobson/commands](https://github.com/wshobson/commands) | MIT | prompts |
| [github/awesome-copilot](https://github.com/github/awesome-copilot) | MIT | hooks, instructions |
| [PatrickJS/awesome-cursorrules](https://github.com/PatrickJS/awesome-cursorrules) | CC0-1.0 | rules |

A few deliberate scoping notes:

- **Rules from awesome-cursorrules install on Cursor only.** Their `.mdc` frontmatter is not strict YAML, so Claude's `.mdc` → rule conversion cannot parse it; Cursor copies the file verbatim.
- **Only one `instructions` asset applies per provider.** Instructions target the whole `CLAUDE.md` / `AGENTS.md` file, so a second replaces the first.
- **Some hooks are provider-scoped** to the tool payload their script parses, so a hook installs only where it can actually work.

## Safety and review

- External assets are pinned to reviewed commit SHAs and checksummed.
- Hooks and MCP definitions pass through the same catalog PR gate as every other asset.
- Local modifications are detected before updates and backed up under `.agentpack/backups/`.
- Shared provider configuration is merged; unrelated entries are preserved.
- Assets marked `blocked` cannot be installed.
- Secret values never enter the catalog; MCP manifests declare environment-variable names only.
- Local submissions preview every included file, exclude generated folders, and reject symlinks, private keys, credential files, and oversized folders before cloning the catalog.

## Troubleshooting

**"No provider configuration detected"** — name a provider with `--claude`, `--codex`, `--copilot`, or `--cursor`, and run `agentpack doctor` to see what is detected.

**"This catalog requires AgentPack &lt;version&gt; or newer"** — a catalog can set `minimumAgentPackVersion`; update the CLI with `dotnet tool update -g AgentPack`.

**`agentpack submit` stops before cloning** — it needs the [GitHub CLI](https://cli.github.com/): install `gh`, run `gh auth login`, then retry, or use `--prepare-only`.

**Catalog looks stale, or you are offline** — read-only commands use a cache and refresh it after 24 hours. Force it with `agentpack catalog sync`; offline, AgentPack falls back to the cache and warns instead of failing.

**Where did my files go?** — run `agentpack config` to see the home and provider home, and `agentpack status` to list installs.

## Documentation

| Document | Read it for |
|---|---|
| [CLI reference](docs/cli-reference.md) | Every command, option, environment variable, and exit code |
| [Catalog authoring](docs/catalog-authoring.md) | Writing an asset manifest by hand and what reviewers check |
| [Groups and profiles](docs/groups-profiles.md) | Grouping assets and applying a team's set in one command |
| [How AgentPack works](docs/how-it-works.md) | Resolution, install, drift detection, and lockfiles |
| [Provider mapping](docs/provider-mapping.md) | Where each asset kind lands per provider |
| [External assets](docs/external-assets.md) | Pinning, checksums, and licensing of upstream content |
| [Governance](docs/governance.md) | Review gates, sourcing policy, and ownership |
| [Catalog repository setup](docs/catalog-repository-setup.md) | Running your own catalog repository |
| [CLI design](docs/cli-design.md) | The command surface and the principles behind it |

## How AgentPack is versioned

AgentPack has three deliberately separate parts, so a catalog change does not require a CLI release:

| Part | Purpose | How it updates |
|---|---|---|
| AgentPack NuGet package | Installs the `agentpack` CLI | Publish a new NuGet version |
| Catalog Git repository | Stores approved assets and review history | Merge a catalog pull request |
| User or project files | Copies selected assets into each tool's native format | `agentpack install` / `agentpack update` |

## Install and update the CLI

```bash
dotnet tool install -g AgentPack
dotnet tool update -g AgentPack
agentpack --version
```

The global tool requires the .NET SDK. Self-contained binaries that need no .NET runtime are published on the [GitHub Releases page](https://github.com/svennijhuis/AgentPack/releases) for Windows, Linux, and macOS on x64 and ARM64.

## Development

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes

# Run the local CLI without installing it
dotnet run --project src/AgentPack.Cli -- --help
dotnet run --project src/AgentPack.Cli -- list
```

Exit codes: `0` success · `1` user error · `2` validation failure · `3` drift/conflict · `70` internal error.

## License

AgentPack is open source under the [MIT License](LICENSE).
