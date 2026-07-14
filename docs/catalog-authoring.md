# Catalog Authoring

## Principles

1. **Everything derivable is derived.** `id` and `kind` come from the folder path (`assets/<kind>/<id>/`), content lives in `content/`, checksums live in the generated `catalog.lock.yaml`. Authors write only what a human must decide.
2. **All changes are PRs.** The CLI scaffolds; humans review; CI validates. Nothing enters the catalog without a second pair of eyes.
3. **Secrets never enter the catalog.** MCP env vars are declared by name; installs render `${VAR}` placeholders.

## Add a local asset

```bash
agentpack new skills grill-me --group review
```

creates:

```text
assets/skills/grill-me/
  agentpack.yaml
  content/SKILL.md
```

Manifest fields:

```yaml
name: Grill Me                    # display name
version: 1.0.0                    # bump on every content change (semver)
description: When to use this.    # shown in list/add UIs — write it well
groups: [engineering, review]     # discovery labels ('agentpack list -g review')
# providers omitted = available for all providers
# providers: [claude, codex]     # only to restrict
# owner: platform-team           # optional; CODEOWNERS is the real gate
status: experimental              # experimental | recommended | deprecated | blocked
channel: internal                 # internal | beta | stable
```

Kind-specific extras:

```yaml
# hooks (assets/hooks/<id>/agentpack.yaml) — content/hook.sh is the entrypoint
hook:
  trigger: preToolUse             # preToolUse | postToolUse | stop | sessionStart | userPromptSubmit | notification
  tool: Bash                      # matcher (Claude Code)
  command: hook.sh
  timeoutSec: 30

# mcp (assets/mcp/<id>/agentpack.yaml) — no content folder needed
mcp:
  server: github
  transport: stdio                # stdio | http | sse
  command: github-mcp-server
  envVars: [GITHUB_TOKEN]         # names only, never values
  tools: [search, get_issue]       # required when an agent imports this MCP
```

## Author a native agent

```bash
agentpack new agents dotnet-upgrade \
  --description "Plans and implements safe, evidence-based .NET upgrades." \
  --tool read --tool search --tool edit --tool execute --tool web \
  --instruction dotnet-conventions \
  --skill dependency-analysis \
  --mcp microsoft-docs
```

The top-level `description` is required for agents and is rendered into every native format. The typed agent-specific contract is:

```yaml
agent:
  tools: [read, search, edit, execute, web]
  imports:
    instructions:
      - id: dotnet-conventions
        version: ">=1.0.0 <2.0.0"
    skills:
      - id: dependency-analysis
        version: ">=1.2.0 <2.0.0"
    mcp:
      - id: microsoft-docs
        version: ">=1.0.0 <2.0.0"
```

Use scalar shorthand when no compatibility constraint is needed: `skills: [dependency-analysis]`. The effective catalog has one version of each id; a range validates that version and never downloads arbitrary historical releases. Every import resolves to exactly one required kind (`instructions`, `skills`, or `mcp`), must support every agent provider, and cannot be blocked or deprecated. Duplicate, missing, ambiguous, wrong-kind, provider-limited, and incompatible references fail validation with a stable `agent.dependency.*` code.

`agent.tools` names portable built-in capabilities (`read`, `search`, `edit`, `execute`, `web`, `agent`); it does not install executables. Claude and Copilot support exact granular allowlists. Codex and Cursor receive their native coarse sandbox/read-only projection and otherwise inherit provider tools. Package custom tools as MCP assets and declare the exact `mcp.tools` inventory. The reserved generic `tools` asset kind has no installation contract and fails validation.

Models are intentionally not part of the agent contract. Compilation removes model metadata found in imported frontmatter or the legacy `agent.models` mapping and reports a warning. Every generated agent therefore uses the model already selected by the user, session, or workflow. See [agent-authoring.md](agent-authoring.md) for a complete manifest, source prompt, capability matrix, and all four generated formats.

Imported instructions are private composition inputs, not installs into `CLAUDE.md`, `AGENTS.md`, or `.github/instructions`. External agent frontmatter is discarded; only dependencies in this manifest are trusted.

## Add an external asset

```bash
agentpack import https://github.com/anthropics/skills/tree/main/skills/pdf@<commit-sha>
```

- The `@<ref>` **must** be a full commit SHA or an immutable tag. Branches are rejected — you are approving exact content, not a moving target.
- Read the upstream content at that ref before opening the PR; the PR review approves that exact code.
- Record the upstream license with `--license MIT` when known; validation warns when missing.
- Hook triggers/commands and MCP server/tool inventories remain typed AgentPack metadata; they are never inferred from untrusted upstream frontmatter. See [external-assets.md](external-assets.md) for every supported external kind and its fetched-content contract.

## Versioning

- Bump `version` on every content change; consumers see updates via `agentpack outdated`.
- `status: deprecated` warns on install; `status: blocked` refuses to install (and errors if requested explicitly).
- Deprecated groups need `replacedBy` + `removeAfter` — validation enforces it.

## CI for the catalog repo

```yaml
on: pull_request
jobs:
  validate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      - run: dotnet tool install -g AgentPack --add-source <your-feed>
      - run: agentpack catalog validate
      - run: agentpack catalog lock --check          # fails when checksums are stale
      - run: agentpack catalog verify-external       # fetches pinned refs, verifies hashes
      - run: agentpack catalog compile               # resolves and parses all native agent outputs
      - run: dotnet test
```

On merge to main, run `agentpack catalog lock` and commit the refreshed `catalog.lock.yaml` (or require authors to run it locally — `--check` keeps them honest).

Recommended CODEOWNERS:

```text
assets/**            @org/ai-platform
assets/hooks/**      @org/security
```

Hooks execute code on developer machines and any external source change pulls third-party code — route both to security review.

## Team overlays

A consuming repo can add team-local assets without touching the org catalog: put `.agentpack/catalog.yaml` and `.agentpack/assets/<kind>/<id>/` in the repo. Overlay entries with the same id override the org catalog for that repo, making a deliberate customization a typed, reviewable effective asset instead of an unverifiable edit to a generated file. See [team-overlays.md](team-overlays.md).
