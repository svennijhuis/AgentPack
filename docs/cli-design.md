# CLI Design

## Principles

1. **Interactive when you are, scriptable when you're not.** A real terminal gets checklists, confirmations, and drift prompts; CI (`CI` env var, redirected stdin, or `--yes`) gets deterministic flag-driven behavior. Interactivity is additive — no script ever blocks on a prompt.
2. **Never destroy silently.** Local modifications prompt (overwrite / keep / diff / abort); every overwrite backs up first; merge conflicts are errors, not overwrites.
3. **Every error carries a next step.** `error:` + `hint:` lines; unknown commands and asset ids get "did you mean" suggestions.
4. **Explicit over invented.** Provider/kind combinations that don't exist show `skipped ... : <reason>` instead of pretending.
5. **Output scales with the catalog.** Long lists collapse instead of scrolling: the interactive picker switches to a category browser when the catalog outgrows one page, plan/apply output folds "already up to date" rows into a single count, and `list` drops columns that would say the same thing on every row.

## Long lists

- **Interactive picker (`add`/`upgrade` with no args).** Small catalogs get a single grouped checklist (page size adapts to the terminal height). Larger catalogs get a category browser: a searchable menu of kinds with counts ("skills (12)"), plus "everything", "Done", and "Cancel". Picking a kind opens the checklist for just that kind; selections go into a cart that survives switching kinds ("skills (3 of 12 selected)", "Done — 5 selected"). "Done" applies the cart. Upgrade preselects every outdated entry in both modes.
- **Plan output.** Rows that need no work (up to date, pinned) are hidden once there are more than five, replaced by one summary line; a "To do:" line summarizes the actionable rows of a large plan. Skipped provider/kind combinations that share a reason are grouped into one line.
- **Apply output.** Per-row "already up to date"/"pinned" lines collapse into counts past the same threshold, and large runs end with a one-line summary ("Done: 3 installed, 1 updated").
- **`list`/`status`.** `list` hides the Status and Source columns when every row would show the default, truncates descriptions to the terminal width, and prints a count-plus-filter hint footer for 10+ assets. `status` colors actionable states, summarizes updates in a footer, and explains the next step when nothing is installed yet.

## Command surface

```text
agentpack add [kind] [id...] [-g group] [--claude|--codex|--copilot|--cursor|-p name]
              [--user|--project] [--yes] [--force|--keep-local]
agentpack plan ...                          # add, dry-run
agentpack upgrade [kind] [id...] / outdated
agentpack remove <id...> / status / diff <id> / pin <id> / unpin <id>
agentpack new <kind> <id> [--group g] [--provider p] [--name n] [--description d]
agentpack import <url[@ref]> [--ref sha] [--kind k] [--id i] [--license L]
agentpack profile list|plan|apply <id>
agentpack catalog validate | lock [--check|--no-fetch] | verify-external
agentpack source add|list|sync
agentpack groups / doctor
```

Defaults chosen for the common case:

- **Providers**: detected from the repo; flags override.
- **Scope**: project inside a git repo, user otherwise; `--user`/`--project` override.
- **`add` with no args**: interactive multi-select of everything installable — including hooks and MCP — grouped by kind.
- **`upgrade` with no args**: interactive multi-select of outdated entries, preselected.

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
