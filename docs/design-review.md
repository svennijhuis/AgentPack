# Design Review

> **Historical document** — describes the 0.1.0 design process. The current design is in README.md and docs/. Superseded items: bundles were removed, checksums moved to catalog.lock.yaml, external assets use the url@ref shorthand.

## What We Borrowed

- Homebrew taps keep installable definitions in clear subdirectories instead of mixing everything at the repository root.
- asdf separates a shortname catalog from the actual plugin repositories and recommends reviewing external code before use.
- Anthropic and OpenAI skills are self-contained folders with `SKILL.md` plus optional scripts, references, examples, and assets.
- Microsoft's skills tooling focuses on a wizard-style install flow and provider-native destinations.

## What Was Bad In The First Draft

- External assets had too much YAML: `repo`, `ref`, `path`, `checksum`, `license`, and optional upstream fields.
- The name `hub` was too generic.
- It was not obvious where teams should add their own skills/hooks/MCP configs.
- Skills looked like single files, but real skills are folder packages.
- Too much work was manual: creating folders, writing manifests, calculating checksums.

## Fixes Applied

- Product and command are now `AgentPack` / `agentpack`.
- Local assets live in `assets/<kind>/<id>/agentpack.yaml` plus `content/`.
- Team overlays live in `.agentpack/assets/<kind>/<id>/agentpack.yaml` plus `content/`.
- External assets require only `source.url` and `source.ref`; `source.version` remains a deprecated alias.
- `agentpack new <kind> <id>` scaffolds local assets and calculates checksums.
- `agentpack new ... --external-url ... --ref ...` scaffolds external references.
- Skills scaffold `content/SKILL.md`, `content/references/`, and `content/examples/`.
- MCP and hook assets install through provider-native config writers instead of copied placeholder folders.

## Outcome

The recommendations from this historical review have since been implemented or superseded: the CLI has scalable interactive pickers and drift prompts, the codebase uses AgentPack naming, generated checksums live in `catalog.lock.yaml`, and remove operations unmerge managed MCP and hook fragments safely. Current behavior and remaining product decisions are documented in the main README and topic guides rather than tracked here.
