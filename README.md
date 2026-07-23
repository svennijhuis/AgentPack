# AgentPack

**One friendly place for your AI skills, hooks, MCP servers, prompts, agents, and instructions.**

I like collecting useful AI tools, but installing the same files in different formats—and remembering where each one came from—quickly becomes messy. AgentPack gives those assets one reviewed catalog and one simple install command. Use the public catalog, point AgentPack at your team's catalog, or submit something useful for everyone.

AgentPack is open source. Contributions are welcome, and external creators keep their attribution: catalog entries record the original repository, exact reviewed commit, and license information when available. Thank you to the people who publish the skills and tools that make this ecosystem useful.

AgentPack has three deliberately separate parts:

| Part | Purpose | How it updates |
|---|---|---|
| AgentPack NuGet package | Installs the `agentpack` CLI | Publish a new NuGet version |
| Catalog Git repository | Stores approved assets and review history | Merge a catalog pull request |
| User or project files | Copies selected assets into each AI tool's native format | Run `agentpack install` or `agentpack update` |

A catalog change does **not** require a NuGet release. NuGet ships the application; Git ships the catalog.

## Install an asset

```bash
dotnet tool install -g AgentPack
agentpack list
agentpack install grill-me --user
```

AgentPack has a built-in official catalog, so a normal user does not configure a source first. `agentpack list` shows everything it currently ships; `agentpack search <query>` narrows it down. To install the same asset into the current repository instead:

```bash
agentpack install grill-me --project
```

The asset always comes from the approved catalog. The scope only controls its destination:

| Scope | Meaning |
|---|---|
| `--user` | Available to you across projects |
| `--project` | Installed into the current repository for the team |

User installations are local to the current device. Run the same command on another computer to install there; both devices use the same catalog as their source of truth. Project installations live in the repository and can be committed for the team when the target provider expects project files.

If neither flag is supplied, AgentPack uses project scope inside a Git repository and user scope elsewhere.

Providers are detected from the directory the install writes to: your home directory for `--user`, the repository for `--project`. Provider flags such as `--codex`, `--claude`, `--copilot`, and `--cursor` override that detection, and are required only when AgentPack finds no provider in either place.

Preview an installation without writing files:

```bash
agentpack install grill-me --codex --user --dry-run
```

## Submit an asset to the catalog

`submit` means “propose this asset for review.” It never pushes directly to `main`.

Submit a local skill:

```bash
agentpack submit skill ./my-skill
```

A skill is normally a folder. AgentPack requires `SKILL.md` at its top level and includes its supporting folders such as `scripts/`, `references/`, and `assets/`.

Hooks can be one script or a folder:

```bash
# One file: the command is inferred
agentpack submit hook ./check-secrets.sh --trigger preToolUse --tool Bash

# A folder: inferred when it contains one clear script, otherwise say which file starts it
agentpack submit hook ./secret-check --command scripts/check.sh
```

MCP servers are submitted as typed configuration, not as an arbitrary folder. Secrets are never arguments or catalog values; only their environment-variable names are recorded:

```bash
# Local process (stdio)
agentpack submit mcp github \
  --command github-mcp-server \
  --arg stdio \
  --env GITHUB_TOKEN

# Remote server
agentpack submit mcp company-docs \
  --url https://mcp.example.com \
  --header-env Authorization=COMPANY_MCP_TOKEN
```

The input rules match how each kind is actually installed:

| Kind | Submit input |
|---|---|
| Skill | Folder with top-level `SKILL.md`; supporting files and folders are included |
| Hook | One script, or a folder plus one reviewed entry command |
| Instruction, rule, prompt, agent | One file, because providers install these as one native file |
| MCP server | ID plus typed `--command` or `--url` settings; no arbitrary folder |
| Tool, template | Not accepted yet; no supported provider-native destination exists |

Submit an external skill:

```bash
agentpack submit skill \
  https://github.com/mattpocock/skills/tree/main/skills/productivity/writing-great-skills
```

The short form is intentional: AgentPack derives the ID from the URL and pins the latest commit currently on that branch. Override only when needed:

```bash
agentpack submit skill <url> --id writing-great-skills
agentpack submit skill <url> --ref <full-commit-sha>
agentpack submit skill <url> --version 1.2.0
```

AgentPack then:

1. previews the exact local files or external source that will be proposed;
2. asks for confirmation before publishing;
3. clones the active catalog;
4. creates an `agentpack/submit/...` branch;
5. resolves an external source to a full commit SHA;
6. adds the asset manifest or reviewed local content;
7. records the asset's checksum in `catalog.lock.yaml`, leaving every other entry untouched;
8. validates the complete catalog;
9. commits and pushes only the proposal branch—to the catalog for maintainers, or automatically to the contributor's fork when they have read-only access;
10. opens a pull request with the GitHub CLI.

### Keep an asset up to date

Iterating on an asset you already contributed uses the same command with `--update`:

```bash
agentpack submit skill ./my-skill --update
```

That opens a pull request replacing the asset's folder with your current one and bumps the patch version so existing installs pick it up on `agentpack update`. Pass `--version` to choose a minor or major bump instead; the version must always be higher than the one in the catalog. Submitting an asset that already exists without `--update` fails before anything is cloned, and so does `--update` for an asset that is not in the catalog yet.

For local folders, AgentPack ignores common repository/build clutter (`.git`, `node_modules`, `bin`, `obj`, caches), rejects symlinks and secret-like files, and limits the number and total size of files. The preview is the authoritative list: if a file is not shown there, it is not copied into the proposal. In scripts or CI, `--yes` accepts that preview; `--prepare-only` never publishes it.

Automatic PR creation requires [`gh`](https://cli.github.com/) and `gh auth login`. AgentPack checks for both before asking you to confirm, so an unauthenticated run stops before anything is cloned. Add `--draft` to open the pull request as a draft. To prepare and inspect the branch without pushing or needing `gh` at all:

```bash
agentpack submit skill ./my-skill --prepare-only
```

The asset becomes available only after its PR is approved and merged. Contributors do not need write access to the catalog: AgentPack creates or reuses their GitHub fork. Catalog branch protection and CODEOWNERS provide the approval boundary; catalog authors and maintainers use the same submission command and never push directly to `main`.

## Catalog updates

- `agentpack install` and `agentpack update` refresh a remote catalog before changing files.
- Read-only browsing uses the cache and refreshes it when it is older than 24 hours.
- If refresh fails while offline, AgentPack uses a cached catalog and prints a warning.
- `agentpack catalog sync` forces an immediate refresh.
- `agentpack catalog status` shows the repository, branch, revision, cache, and last refresh.

Example:

```bash
agentpack catalog status
agentpack catalog sync
agentpack update --user
```

Publish a new NuGet version only when CLI behavior changes—for example commands, provider adapters, validation, catalog schema, or supported asset kinds. Catalog content changes are published by merging their PR. `minimumAgentPackVersion` prevents a catalog that needs a newer CLI from being used by an older installation.

## Use another approved catalog

The official catalog works without setup. An organization can select its own catalog once:

```bash
agentpack catalog use https://github.com/your-org/ai-catalog.git --name company
```

The command validates the repository by syncing it immediately. Organization-managed machines can instead set `AGENTPACK_CATALOG_URL` and optional `AGENTPACK_CATALOG_BRANCH`.

One active catalog keeps the trust model understandable. The catalog is always the source; `--user` and `--project` only choose where an approved asset is installed.

## Everyday commands

| Goal | Command |
|---|---|
| Search the catalog | `agentpack search <query>` |
| Browse the catalog | `agentpack list` |
| Install an asset | `agentpack install <id> [--user\|--project]` |
| Preview installation | `agentpack install <id> --dry-run` |
| Submit an asset for review | `agentpack submit <kind> <path-or-url-or-id>` |
| Show installed assets | `agentpack status` |
| Update installed assets | `agentpack update` |
| Remove an installed asset | `agentpack remove <id>` |
| Inspect the active catalog | `agentpack catalog status` |
| Refresh the catalog now | `agentpack catalog sync` |

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

“—” means the provider has no corresponding feature. AgentPack reports the skip instead of inventing a format. See [provider mapping](docs/provider-mapping.md) for paths and translations.

## Safety and review

- External assets are pinned to reviewed commit SHAs and checksummed.
- Hooks and MCP definitions go through the same catalog PR gate as every other asset.
- Local modifications are detected before updates and backed up under `.agentpack/backups/`.
- Shared provider configuration is merged; unrelated entries are preserved.
- Catalog assets marked `blocked` cannot be installed.
- Secret values never enter the catalog; MCP manifests declare environment-variable names only.
- Local submissions preview every included file, exclude common generated folders, and reject symlinks, private keys, credential files, and oversized folders before cloning the catalog.

## Documentation

| Document | Read it for |
|---|---|
| [CLI reference](docs/cli-reference.md) | Every command, option, environment variable, and exit code |
| [Catalog authoring](docs/catalog-authoring.md) | Writing an asset manifest by hand and what reviewers check |
| [Groups and profiles](docs/groups-profiles.md) | Grouping assets and applying a team's set in one command |
| [How AgentPack works](docs/how-it-works.md) | Resolution, install, drift detection, and lockfiles |
| [Provider mapping](docs/provider-mapping.md) | Where each asset kind lands per provider |
| [External assets](docs/external-assets.md) | Pinning, checksums, and licensing of upstream content |
| [Governance](docs/governance.md) | Status lifecycle, ownership, and deprecation |
| [Catalog repository setup](docs/catalog-repository-setup.md) | Running your own catalog repository |
| [CLI design](docs/cli-design.md) | The command surface and the principles behind it |

## Advanced catalog maintenance

Catalog maintainers and CI can use the lower-level commands directly:

```bash
agentpack catalog validate
agentpack catalog lock
agentpack catalog verify-external
```

Normal contributors never edit manifests or lockfiles manually; `agentpack submit` prepares those files on the proposal branch. The maintenance commands above exist for catalog CI and incident recovery.

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
```

Run the local CLI without installing it:

```bash
dotnet run --project src/AgentPack.Cli -- --help
dotnet run --project src/AgentPack.Cli -- catalog status
```

Build and install a local NuGet package:

```bash
dotnet pack src/AgentPack.Cli -c Release -o ./artifacts/packages -p:Version=0.0.1-dev.1
dotnet tool install -g AgentPack --add-source ./artifacts/packages --version 0.0.1-dev.1
```

Exit codes: `0` success · `1` user error · `2` validation failure · `3` drift/conflict · `70` internal error.

## License

AgentPack is open source under the [MIT License](LICENSE).
