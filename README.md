# AgentPack

**One catalog of approved AI assets. Every developer. Every AI tool. One command.**

`agentpack` is a .NET global tool that installs your organization's agents, skills, hooks, MCP servers, instructions, rules, and prompts into **Claude Code, Codex, GitHub Copilot, and Cursor** — each in that product's own native format, so everything works out of the box.

```bash
agentpack add
```

```
? Select assets to install (space toggles, enter confirms)
  skills
  ◉ grill-me      1.0.0  Challenges a plan with critical review questions
  hooks
  ◉ secret-scan   1.0.0  Blocks likely secrets before tool execution
  mcp
  ◯ github        1.0.0  GitHub MCP server (needs GITHUB_TOKEN)

✓ installed grill-me (claude, codex, copilot, cursor) 1.0.0
✓ installed secret-scan (claude, codex, copilot, cursor) 1.0.0
```

## What it manages

Everything works on all four providers unless the product itself has no such concept:

| Kind | What it is | Claude | Codex | Copilot | Cursor |
|---|---|:-:|:-:|:-:|:-:|
| **agents** | native custom/subagents with automatic dependencies | ✓ | ✓ | ✓ | ✓ |
| **skills** | agent skills (SKILL.md folders) | ✓ | ✓ | ✓ | ✓ |
| **hooks** | pre/post tool-use scripts | ✓ | ✓ | ✓ | ✓ |
| **mcp** | MCP server configs | ✓ | ✓ | ✓ | ✓ |
| **instructions** | CLAUDE.md / AGENTS.md / Copilot instructions | ✓ | ✓ | ✓ | ✓ |
| **prompts** | reusable prompts / slash commands | ✓ | ✓ | ✓ | ✓ |
| **rules** | Cursor rules (.mdc) | — | — | — | ✓ |
| **profiles** | everything a team needs, in one command | ✓ | ✓ | ✓ | ✓ |

"—" = the product has no such feature; agentpack says so explicitly instead of writing files nothing reads. Every path is verified against official docs and pinned by tests: [provider-mapping.md](docs/provider-mapping.md).

## Setup (once)

### Install the released CLI

```bash
# 1. Install the CLI from nuget.org (requires the .NET SDK)
dotnet tool install -g AgentPack

# 2. Point it at your company's asset catalog — the git repo with the approved skills/hooks/mcp
agentpack source add org https://github.com/your-org/ai-catalog.git
```

Update later with `dotnet tool update -g AgentPack`. If your organization mirrors the tool on an internal feed instead, add `--add-source https://nuget.your-org.com/v3/index.json` to both commands.

That's all — the catalog syncs automatically on first use. Two different "sources", two different things: the **NuGet feed** delivers the `agentpack` tool itself; the **catalog source** is the git repo the tool installs assets from.

### Or download a standalone binary (no .NET required)

Each tagged release attaches self-contained executables to the [GitHub Releases page](https://github.com/svennijhuis/AgentPack/releases) — no .NET SDK or runtime needed:

```bash
# macOS (Apple Silicon) / Linux: download, extract, put on PATH
tar -xzf agentpack-<version>-osx-arm64.tar.gz
sudo mv agentpack /usr/local/bin/

# Windows: unzip agentpack-<version>-win-x64.zip and add the folder to PATH
```

Available for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.

Even step 2 is skippable:

- **Inside the catalog repo** (like this one — `catalog.yaml` + `assets/` live here): nothing to configure, it's picked up automatically.
- **Org-wide default:** set `AGENTPACK_CATALOG_URL=https://github.com/your-org/ai-catalog.git` in your machine-setup / shell profile and agentpack registers and syncs it by itself.

Works on Windows (PowerShell), macOS, and Linux.

### Develop the CLI from this GitHub repo

Use the NuGet feed when you want the released `agentpack` command. Use the GitHub repo when you are changing the CLI itself. A repo checkout is not a NuGet feed, so either run from source or pack a local tool package first:

```bash
git clone https://github.com/your-org/agentpack.git
cd agentpack

# Fastest while iterating: run the CLI directly from source
dotnet run --project src/AgentPack.Cli -- list
dotnet run --project src/AgentPack.Cli -- add grill-me

# Optional: install this checkout as your global `agentpack` command
dotnet pack src/AgentPack.Cli -c Release -o ./artifacts/packages -p:Version=0.3.0-dev.1
dotnet tool uninstall -g AgentPack
dotnet tool install -g AgentPack --add-source ./artifacts/packages --version 0.3.0-dev.1
```

If `dotnet tool uninstall` says the tool is not installed, continue with the install step. When you want to reinstall a newer local build, pack it with a new dev version such as `0.3.0-dev.2`, then run:

```bash
dotnet tool update -g AgentPack --add-source ./artifacts/packages --version 0.3.0-dev.2 --allow-downgrade
```

Running the CLI from this repo only changes where the `agentpack` command comes from. The asset catalog is still the repo with `catalog.yaml` and `assets/`; if you run inside that catalog repo, agentpack auto-detects it.

## Use it

### I want the tools my team uses

```bash
agentpack profile apply backend      # everything your team standardized on
```

### I want a native agent everywhere

```bash
agentpack add dotnet-upgrade --claude --codex --copilot --cursor
agentpack add agent-governance-reviewer --claude --codex --copilot --cursor
```

That one request resolves the complete typed dependency graph. Imported instructions are composed privately into the generated agent, skills install into each provider's native skill directory, and MCP configuration stays agent-local except Cursor's inherited `.cursor/mcp.json` entry. Built-in `tools` configure provider capabilities; custom tools must be MCP assets. Nothing is installed for a generic `tools` asset.

The tool names are portable capabilities, not strings copied blindly to every file. Claude and Copilot get native granular allowlists; Codex and Cursor get their supported coarse sandbox/read-only projection and otherwise inherit native tools. See [agent authoring](docs/agent-authoring.md) for the exact mapping and generated examples.

Native targets are `.claude/agents/<id>.md`, `.codex/agents/<id>.toml`, `.github/agents/<id>.agent.md`, and `.cursor/agents/<id>.md` (or the corresponding user directories). A project Copilot agent is directly reusable by a GitHub Agentic Workflow:

```yaml
imports:
  - .github/agents/dotnet-upgrade.agent.md
```

### I want to browse and pick

```bash
agentpack add                        # checklist of everything
agentpack add skills                 # checklist of skills only
agentpack add skills --claude        # ...installing just for Claude Code
agentpack add grill-me secret-scan   # skip the checklist: install by name
agentpack add --group security       # checklist of one group
agentpack list                       # just look, install nothing
```

Naming ids installs directly; a kind or group opens the checklist filtered to it.

Groups are **hierarchical** — a top-level language plus optional `language/topic` subgroups. Filtering by a parent includes every subgroup; filter by a subgroup to narrow.

| Command | Installs |
|---|---|
| `agentpack add -g csharp` | everything C# (language skills + shared skills + hooks) |
| `agentpack add -g csharp/review` | just the C# review skills |
| `agentpack add -g csharp/testing` | just the C# testing skills |
| `agentpack list -g react` | browse all React assets |
| `agentpack groups` | the full group tree with asset counts |

Current top-level groups: `react`, `node`, `typescript`, `csharp`, `git`. Common subtopics: `/review`, `/testing`, `/api`, `/format`, `/workflow`.

### I want to stay up to date

```bash
agentpack status                     # what's installed, what has updates
agentpack upgrade                    # update (asks before touching your local edits)
agentpack remove grill-me            # uninstall (backup kept)
agentpack prune                      # preview clean orphaned automatic dependencies
```

That's it. Providers are auto-detected from your repo (`.claude/`, `.cursor/`, `AGENTS.md`, ...); force them with `--claude --codex --copilot --cursor`. Inside a git repo installs are project-scoped; `--user` installs to your home directory instead.

### It never surprises you

- **Your local edits are safe.** If you changed an installed file, agentpack asks: overwrite, keep, show diff, or abort. Never silent.
- **An agent is one transaction.** Its dependencies and native file are staged, syntax-checked, locked, and applied together; any failure restores provider files and the previous lock.
- **Scripts must decide drift.** `--yes` confirms the plan but does not choose what to do with local edits; use `--force` or `--keep-local` (exit `3` otherwise).
- **Your configs are safe.** Merging into `settings.json` / `mcp.json` / `config.toml` only adds entries — existing ones are never touched; a conflict is an error, not an overwrite.
- **Everything is undoable.** Any file agentpack replaces is backed up to `.agentpack/backups/` first.
- **It tells you the next step.** "Set GITHUB_TOKEN in your environment", "commit .github/hooks/", "approve the MCP server when Claude Code asks" — printed right after install.
- **Scripts work too.** `--yes`, `--force`, `--keep-local`; prompts disappear automatically in CI.
- Typos get help: `agentpack statsu` → *Did you mean 'agentpack status'?*

## Add your own asset (5 minutes)

```bash
agentpack new skills grill-me --group review
# → assets/skills/grill-me/agentpack.yaml + content/SKILL.md
```

Create an agent and declare its automatic dependencies without hand-writing YAML:

```bash
agentpack new agents dotnet-upgrade \
  --description "Plans and implements safe .NET upgrades." \
  --tool read --tool search --tool edit --tool execute --tool web \
  --instruction dotnet-conventions \
  --skill dependency-analysis \
  --mcp microsoft-docs
```

AgentPack never pins a model in generated agent files. Imported or legacy model metadata is removed with a warning, so the user's session or workflow keeps its current/default model.

Edit the content, open a PR. Done. The manifest is ~5 lines — everything derivable is derived (id and kind from the folder, checksums generated by CI):

```yaml
name: Grill Me
version: 1.0.0
description: Challenges a plan with critical review questions.
groups: [engineering, review]
# providers omitted = works on all providers
```

### Use something from GitHub

```bash
agentpack import https://github.com/anthropics/skills/tree/main/skills/pdf@<commit-sha>
```

Pinned to the exact commit you reviewed — never a moving branch. Installs verify the content hash; upstream changes only arrive through a new PR that a human re-reviews.

The same command supports external agents, hooks, MCP, instructions, prompts, and Cursor rules. Each kind has a validated content contract; hook permissions and MCP tool inventories stay in reviewed AgentPack metadata. See [external-assets.md](docs/external-assets.md).

## For platform / security teams

The catalog is a git repo. Control comes free with your existing workflow:

- **Every change is a PR.** CI blocks anything invalid: `agentpack catalog validate`, `catalog lock --check`, `catalog verify-external`, and `catalog compile`.
- **CODEOWNERS routes review** — e.g. `assets/hooks/**` and external sources to the security team (hooks execute code on dev machines).
- **Kill switch:** set `status: blocked` on an asset; installs stop immediately.
- **No secrets in the catalog, ever.** MCP env vars are names; each provider gets its own reference syntax; values stay in the user's shell.
- **Profiles** define what each team gets: `agentpack profile apply backend` onboards a new hire in one command.

```text
catalog.yaml            groups + team profiles
catalog.lock.yaml       generated checksums (CI)
assets/
  agents/dotnet-upgrade/ agentpack.yaml + content/AGENT.md (or pinned external source)
  skills/grill-me/      agentpack.yaml + content/SKILL.md
  hooks/secret-scan/    agentpack.yaml + content/hook.sh
  mcp/github/           agentpack.yaml (mcp: section, no content needed)
```

## All commands

| Command | Does |
|---|---|
| `add` / `plan` | install (interactive when no args) / dry-run |
| `upgrade` / `outdated` | update installed assets / just report |
| `remove` / `prune` / `status` / `diff` | uninstall / clean orphans / overview / local-edit check |
| `pin` / `unpin` | hold an asset at its version |
| `new` / `import` | scaffold a local / external asset for a PR |
| `profile list\|plan\|apply` | team bundles |
| `catalog validate\|lock\|verify-external\|compile` | CI checks, including every native agent output |
| `source add\|list\|sync` | use a remote catalog repo |
| `groups` / `list` / `doctor` | discovery and diagnosis |

Exit codes: `0` ok · `1` user error · `2` validation failed · `3` drift/conflict · `70` internal.

## How it works

Diagrams for the whole system — install flow, PR flow, external pinning, state machine: **[docs/how-it-works.md](docs/how-it-works.md)**

## Development

```bash
dotnet build     # warnings are errors
dotnet test      # provider golden files, agent dependencies, rollback, merge formats, CLI end-to-end
```

Docs: [agent authoring](docs/agent-authoring.md) · [how it works](docs/how-it-works.md) · [provider mapping](docs/provider-mapping.md) · [catalog authoring](docs/catalog-authoring.md) · [external assets](docs/external-assets.md) · [groups & profiles](docs/groups-bundles-profiles.md) · [governance](docs/governance.md)
