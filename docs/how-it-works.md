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
        EXT["skills pinned to<br/>reviewed commit SHA"]
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

## Installing: `agentpack install`

Planning is read-only for installed files. Install and update refresh the catalog first (with an offline cache fallback), then applying merges into shared config files, records everything in a lockfile, and never overwrites local edits silently.

```mermaid
sequenceDiagram
    actor Dev as Developer
    participant CLI as agentpack
    participant Cat as Catalog
    participant Lock as .agentpack/lock.json
    participant Prov as Provider files

    Dev->>CLI: agentpack install --user|--project
    CLI->>Cat: refresh active catalog (cached fallback if offline)
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

    CLI->>Prov: backup, then copy tree or merge JSON/TOML
    CLI->>Lock: record version + checksums
    CLI-->>Dev: results + follow-up steps (env vars to set, files to commit)
```

## Contributing: `submit` always creates a PR

The CLI scaffolds, humans review, CI validates. Nothing reaches developer machines without passing this gate.

```mermaid
flowchart LR
    A["agentpack submit<br/>path, URL, or MCP id"] --> P["preview exact files/config<br/>reject unsafe input"]
    P --> B["confirm, then pin external ref<br/>or copy reviewed content"]
    B --> C["create branch + lock + validate + commit"]
    C --> F{"Catalog write access?"}
    F -->|yes| PR["Push proposal branch + open PR"]
    F -->|no| FK["Push to contributor fork + open PR"]
    FK --> PR

    subgraph ci["Catalog CI"]
        V["agentpack catalog validate"]
        L["agentpack catalog lock --check"]
        X["agentpack catalog verify-external"]
    end

    PR --> ci
    ci -->|all green| R["CODEOWNERS review<br/>hooks & external → security team"]
    ci -->|red| B
    R -->|approve + merge| M["main = new catalog version"]
    M --> U["developers see it in<br/>agentpack list / outdated / update"]
```

## External assets: pinned, hashed, re-reviewed

AgentPack follows an upstream branch only once during `submit`, immediately resolves it to a commit SHA, and stores that immutable pin. Installs never follow the branch. A ref bump is a new PR, so third-party changes always get human eyes.

```mermaid
sequenceDiagram
    actor Author
    participant Repo as Catalog repo
    participant CI as Catalog CI
    participant Up as Upstream repo
    actor Dev as Developer

    Author->>Repo: submit URL (resolved to reviewed SHA)
    Repo->>CI: PR opened
    CI->>Up: fetch at pinned SHA
    CI->>CI: hash content → catalog.lock.yaml
    CI-->>Repo: merge when green + approved

    Dev->>Up: agentpack install → clone/fetch at SHA (cached in ~/.agentpack/cache)
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
    Available --> Installed: install
    Installed --> UpdateAvailable: catalog version bumped
    Installed --> LocalChanges: user edits installed copy
    Installed --> Missing: file deleted on disk
    UpdateAvailable --> Installed: update
    UpdateAvailable --> Pinned: pin
    Pinned --> UpdateAvailable: unpin
    LocalChanges --> Installed: overwrite (backup kept)
    LocalChanges --> LocalChanges: keep local
    Missing --> Installed: install (reinstall)
    Installed --> [*]: remove (backup kept)
```

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
    MCP --> M3["Copilot: .vscode/mcp.json servers key<br/>env: dollar-brace env:VAR"]
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
