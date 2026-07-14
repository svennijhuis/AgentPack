# How AgentPack Works

Visual guide to the architecture and the main flows. All diagrams render natively on GitHub.

## Big picture

One catalog repo feeds every developer machine and every AI tool. The CLI translates catalog assets into each provider's native format — nothing is invented, every path is the one the product documents.

```mermaid
flowchart LR
    subgraph catalog["Catalog repo (source of truth)"]
        CY["catalog.yaml<br/>groups + profiles"]
        AS["assets/&lt;kind&gt;/&lt;id&gt;/<br/>agentpack.yaml + content/"]
        LK["catalog.lock.yaml<br/>generated checksums"]
    end

    subgraph ext["External repos (GitHub, Azure DevOps)"]
        EXT["agents and skills pinned to<br/>reviewed commit SHA"]
    end

    subgraph dev["Developer machine"]
        CLI["agentpack CLI"]
    end

    subgraph providers["Provider-native files"]
        CL["Claude Code<br/>.claude/, .mcp.json, CLAUDE.md"]
        CX["Codex<br/>.agents/skills, .codex/"]
        CP["Copilot<br/>.github/, .vscode/, ~/.copilot/"]
        CU["Cursor<br/>.cursor/, AGENTS.md"]
    end

    CY --> CLI
    AS --> CLI
    LK --> CLI
    EXT -- "fetch at pinned SHA<br/>+ checksum verify" --> CLI
    CLI --> CL & CX & CP & CU
```

## Installing: `agentpack add`

Planning is read-only and network-free. Applying merges into shared config files without touching what the user already has, records everything in a lockfile, and never overwrites local edits silently.

```mermaid
sequenceDiagram
    actor Dev as Developer
    participant CLI as agentpack
    participant Cat as Catalog
    participant Lock as .agentpack/lock.json
    participant Prov as Provider files

    Dev->>CLI: agentpack add
    CLI->>Cat: load catalog + infer manifests
    CLI-->>Dev: interactive checklist (skills, hooks, mcp)
    Dev-->>CLI: selection
    CLI->>Lock: compare installed checksums
    CLI-->>Dev: plan table (install / update / local changes / skipped + reason)
    Dev-->>CLI: confirm

    alt installed copy was edited locally
        CLI-->>Dev: overwrite / keep / diff / abort?
        Dev-->>CLI: decision
    end

    CLI->>CLI: resolve typed dependency graph + stage every candidate
    CLI->>CLI: parse generated YAML/TOML + verify hashes
    CLI->>Prov: lock scope, backup, then apply complete transaction
    CLI->>Lock: save only after every item succeeds
    CLI-->>Dev: results + follow-up steps (env vars to set, files to commit)

    alt any apply step fails
        CLI->>Prov: restore every backup
        CLI->>Lock: restore previous lock
    end
```

For an agent, resolution produces a provider-specific closure: private instruction inputs, native skill installs, agent-local MCP (or Cursor's inherited shared MCP), then the generated native agent. Shared dependencies are planned once. All drift, pins, unmanaged targets, environment requirements, and merge conflicts are discovered before writing.

## Agent dependency graph

```mermaid
flowchart LR
    A["agent: dotnet-upgrade"] --> I["instruction: dotnet-conventions<br/>embedded only"]
    A --> S["skill: dependency-analysis<br/>native skill folder"]
    A --> M["MCP: microsoft-docs<br/>agent-local except Cursor"]
    I & S & M --> R["provider renderer"]
    R --> CL["Claude Markdown"] & CX["Codex TOML"] & CP["Copilot agent.md"] & CU["Cursor Markdown"]
```

The graph is typed before planning. Version ranges check the catalog's one effective version. Wrong kinds, incompatible versions/providers, blocked dependencies, missing MCP tool inventories, and unpinned or unchecked external sources stop compilation with a stable error and corrective action.

## Contributing: everything is a PR

The CLI scaffolds, humans review, CI validates. Nothing reaches developer machines without passing this gate.

```mermaid
flowchart LR
    A["agentpack new skills grill-me<br/>or<br/>agentpack import url@sha"] --> B["edit content/<br/>commit on branch"]
    B --> PR["Pull request"]

    subgraph ci["Catalog CI"]
        V["agentpack catalog validate"]
        L["agentpack catalog lock --check"]
        X["agentpack catalog verify-external"]
    end

    PR --> ci
    ci -->|all green| R["CODEOWNERS review<br/>hooks & external → security team"]
    ci -->|red| B
    R -->|approve + merge| M["main = new catalog version"]
    M --> U["developers see it in<br/>agentpack list / outdated / upgrade"]
```

## External assets: pinned, hashed, re-reviewed

AgentPack never follows upstream branches. A ref bump is a PR, so third-party changes always get human eyes.

```mermaid
sequenceDiagram
    actor Author
    participant Repo as Catalog repo
    participant CI as Catalog CI
    participant Up as Upstream repo
    actor Dev as Developer

    Author->>Repo: import url@SHA (reviewed upstream at that SHA)
    Repo->>CI: PR opened
    CI->>Up: fetch at pinned SHA
    CI->>CI: hash content → catalog.lock.yaml
    CI-->>Repo: merge when green + approved

    Dev->>Up: agentpack add → clone/fetch at SHA (cached in ~/.agentpack/cache)
    Dev->>Dev: hash fetched content
    alt hash matches catalog.lock.yaml
        Dev->>Dev: install
    else mismatch (tag moved / force-push)
        Dev->>Dev: hard failure, exit 3, cache discarded
    end
```

## Install states and drift

The lockfile stores a checksum of what was installed. Every plan compares disk against it, so local edits are always detected before anything is overwritten.

```mermaid
stateDiagram-v2
    [*] --> Available: in catalog, not installed
    Available --> Installed: add
    Available --> Installed: adopt byte-identical unmanaged target
    Installed --> UpdateAvailable: catalog version bumped
    Installed --> UpdateAvailable: agent dependency fingerprint changed
    Installed --> LocalChanges: user edits installed copy
    Installed --> Missing: file deleted on disk
    UpdateAvailable --> Installed: upgrade
    UpdateAvailable --> Pinned: pin
    Pinned --> UpdateAvailable: unpin
    LocalChanges --> Installed: overwrite (backup kept)
    LocalChanges --> LocalChanges: keep local
    Missing --> Installed: add (reinstall)
    Installed --> [*]: remove (backup kept)
```

`--yes` confirms a non-interactive plan but never decides drift. A conflict exits `3` until the caller passes `--force` (backup and overwrite) or `--keep-local` (skip the complete affected agent closure). Managed snapshots let the interactive diff compare the last generated version, current local version, and staged candidate. AgentPack does not three-way merge prompts, skill content, or frontmatter. A deliberate customization belongs in `.agentpack/catalog.yaml` plus `.agentpack/assets/...`, where it remains typed and reviewable.

## Ownership, removal, and pruning

Every lock entry records whether it was directly requested and why an automatic install exists, for example `agent:dotnet-upgrade`, `agent:api-builder`, or `profile:backend`. Removing one agent drops only its ownership edge. Shared dependencies remain; newly unreferenced automatic installs become orphans. `agentpack prune` previews them and removes only clean orphans. Locally modified orphans are never automatically deleted, and `remove --keep-local` unregisters a generated agent while leaving its file unmanaged.

Agent-local MCP and embedded instructions disappear with the generated agent because they have no separate installed target. Cursor-global MCP dependencies retain ownership edges and are pruned only when no agent or direct request needs them.

## Render fingerprints and updates

An agent lock entry fingerprints the canonical agent source, normalized manifest and portable tools, every imported id/version/checksum, MCP configuration/tool inventory, provider, and scope. Model metadata is excluded because compilation always strips it and inherits the current/default model. A dependency content change therefore makes the agent outdated without an agent version bump. A plan reports local edits before rebuilding; if both local changes and a dependency-driven rebuild exist, the user must keep or overwrite the generated agent first.

## Guarantee boundary

AgentPack guarantees typed dependency resolution, source integrity, provider compatibility checks, deterministic generated syntax, transactional application, backups, rollback, ownership tracking, and local-change detection. `doctor`, compile, and post-install diagnostics make the remaining runtime requirements explicit. AgentPack cannot guarantee that an external MCP endpoint is online, environment values are actually set, a future provider release retains today's format, or a model chooses to invoke a skill.

## What lands where

One asset, four native formats. Merges add entries; they never rewrite or delete what the user already has (conflict = error, not overwrite).

```mermaid
flowchart TD
    HOOK["hook asset<br/>assets/hooks/secret-scan/"]
    MCP["mcp asset<br/>assets/mcp/github/"]
    SKILL["skill asset<br/>assets/skills/grill-me/"]

    HOOK --> H1["Claude: .claude/settings.json<br/>hooks.PreToolUse + matcher"]
    HOOK --> H2["Codex: .codex/hooks.json<br/>same structure as Claude"]
    HOOK --> H3["Copilot: .github/hooks/secret-scan.json<br/>version 1, bash/powershell"]
    HOOK --> H4["Cursor: .cursor/hooks.json<br/>version 1, camelCase events"]

    MCP --> M1["Claude: .mcp.json<br/>env: dollar-brace VAR"]
    MCP --> M2["Codex: .codex/config.toml<br/>env_vars = [names]"]
    MCP --> M3["Copilot CLI: .github/mcp.json<br/>mcpServers; env: dollar-brace VAR"]
    MCP --> M4["Cursor: .cursor/mcp.json<br/>env: dollar-brace env:VAR"]

    SKILL --> S1["Claude: .claude/skills/grill-me/"]
    SKILL --> S2["Codex: .agents/skills/grill-me/"]
    SKILL --> S3["Copilot: .github/skills/grill-me/"]
    SKILL --> S4["Cursor: .cursor/skills/grill-me/"]
```

Full path matrix with doc links: [provider-mapping.md](provider-mapping.md).

## Codebase layout

Typed core, thin CLI. All string parsing happens once at the YAML boundary; everything behind it is enums, records, and exhaustive switches.

```mermaid
flowchart TD
    subgraph cli["AgentPack.Cli"]
        CMD["Commands/ — one class per verb"]
        UI["Ui/ — tables, prompts, diffs, suggestions"]
    end

    subgraph core["AgentPack.Core"]
        CAT["Catalog/ — typed model, mapper,<br/>validator, lock writer"]
        PROV["Providers/ — one adapter per product,<br/>Supported | Unsupported plans"]
        INST["Install/ — installer, lockfile,<br/>McpMerger + HookMerger"]
        EXTM["External/ — url@ref parsing,<br/>git fetch, checksum verify"]
        PRIM["Primitives/ + Infra/ — SemVersion,<br/>hashing, process runner, errors"]
    end

    CMD --> UI
    CMD --> CAT & PROV & INST & EXTM
    CAT --> PRIM
    PROV --> PRIM
    INST --> PROV
    INST --> PRIM
    EXTM --> PRIM
```

Every cell of the provider matrix and every merge format is pinned by golden tests (`tests/AgentPack.Tests`) — changing what lands on disk is always a deliberate, reviewed decision.
