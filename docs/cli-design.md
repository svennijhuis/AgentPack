# CLI Design

## Principles

1. **Interactive when you are, scriptable when you're not.** A real terminal gets checklists, confirmations, and drift prompts; CI (`CI` env var, redirected stdin, or `--yes`) gets deterministic flag-driven behavior. Interactivity is additive — no script ever blocks on a prompt.
2. **Never destroy silently.** Local modifications prompt (overwrite / keep / diff / abort); every overwrite backs up first; merge conflicts are errors, not overwrites.
3. **Every error carries a next step.** `error:` + `hint:` lines; unknown commands and asset ids get "did you mean" suggestions.
4. **Explicit over invented.** Provider/kind combinations that don't exist show `skipped ... : <reason>` instead of pretending.

## Command surface

```text
agentpack add [kind] [id...] [-g group] [--claude|--codex|--copilot|--cursor|-p name]
              [--user|--project] [--yes] [--force|--keep-local]
agentpack plan ...                          # add, dry-run
agentpack upgrade [kind] [id...] / outdated
agentpack remove <id...> [--force|--keep-local] / prune [--yes]
agentpack status / diff <id> / pin <id> / unpin <id>
agentpack agent explain [id]
agentpack new <kind> <id> [--group g] [--provider p] [--name n] [--description d]
              [--tool t]
              [--instruction id] [--skill id] [--mcp id]
agentpack import <url[@ref]> [--ref sha] [--kind k] [--id i] [--license L]
                 [--tool t]
                 [--hook-trigger e] [--hook-tool t] [--hook-command c] [--hook-timeout s]
                 [--mcp-server n] [--mcp-transport t] [--mcp-command c|--mcp-url u]
                 [--mcp-arg a] [--mcp-env NAME] [--mcp-header-env HEADER=NAME]
                 [--mcp-tool t] [--mcp-cwd path]
agentpack profile list|plan|apply <id>
agentpack catalog validate | lock [--check|--no-fetch] | verify-external | compile
agentpack source add|list|sync
agentpack groups / doctor
```

Defaults chosen for the common case:

- **Providers**: detected from the repo; flags override.
- **Scope**: project inside a git repo, user otherwise; `--user`/`--project` override.
- **`add` with no args**: interactive multi-select of everything installable — including hooks and MCP — grouped by kind.
- **`upgrade` with no args**: interactive multi-select of outdated entries, preselected.
- **`prune`**: previews automatic dependencies with no remaining owners; only clean orphans are removable.
- **External kind contracts**: `tools` and `templates` are rejected; hooks require typed trigger/command metadata; agent-imported MCP requires a typed tool inventory.
- **Agent authoring**: an interactive terminal offers provider and portable-tool selectors, previews exact versus coarse enforcement, and keeps all providers selected by default. `agent explain` opens an agent picker when its id is omitted.
- **Models**: there is no model option. Compilation strips upstream and legacy model metadata with a warning; the user's session or workflow keeps its current/default model.

## Drift decisions

Interactive drift offers overwrite, keep local, show diff, and abort. AgentPack never attempts a three-way merge of generated prompts, skills, or agent frontmatter. Existing unmanaged content is adopted without rewriting only when its checksum exactly matches the candidate.

In scripts, `--yes` answers the plan confirmation only. Local changes or a differing unmanaged target require one explicit policy:

```bash
agentpack add dotnet-upgrade --yes --force       # back up and overwrite
agentpack add dotnet-upgrade --yes --keep-local  # skip its complete dependency transaction
```

Without either flag the command exits `3` before writing. A pinned automatic dependency that must change also exits `3` and names `agentpack unpin <id>` as the corrective action.

## Compile diagnostics

`agentpack catalog compile` resolves every agent dependency and renders project/user outputs for every declared provider without installing them. Agent failures use stable codes such as `agent.dependency.version` and include the agent, dependency, provider, current state, and a corrective action. Generated Markdown frontmatter and Codex TOML are syntax-checked before success.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | success |
| 1 | user error (bad arguments, unknown id) |
| 2 | validation failed (catalog errors, stale lock) |
| 3 | drift/conflict (aborted drift prompt, merge conflict, checksum mismatch) |
| 70 | internal error (bug — rerun with `AGENTPACK_DEBUG=1`) |

## Implementation notes

- Spectre.Console + Spectre.Console.Cli: typed settings classes, generated `--help`, styled tables/prompts that degrade to plain text when redirected. Works in Windows PowerShell, Windows Terminal, and macOS/Linux terminals.
- One command class per verb under `src/AgentPack.Cli/Commands/`; rendering isolated in `Ui/`.
- `AgentPackException(message, hint, exitCode)` is the only expected-error channel; the top-level handler renders it without a stack trace.
