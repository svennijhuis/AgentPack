# Catalog Authoring

## Principles

1. **Everything derivable is derived.** `id` and `kind` come from the folder path (`assets/<kind>/<id>/`), content lives in `content/`, checksums live in the generated `catalog.lock.yaml`. Authors write only what a human must decide.
2. **All changes are PRs.** The CLI scaffolds; humans review; CI validates. Nothing enters the catalog without a second pair of eyes.
3. **Secrets never enter the catalog.** MCP env vars are declared by name; installs render `${VAR}` placeholders.

## Add a local asset

```bash
agentpack init                         # once, in a dedicated catalog repository
agentpack new skills grill-me --group review
```

creates:

```text
assets/skills/grill-me/
  agentpack.yaml
  content/SKILL.md
```

Everything under `content/` installs as-is. A skill can carry optional
tool-specific extras (scripts, references, Codex's `agents/openai.yaml`) —
they are preserved byte-for-byte, and `SKILL.md` alone is always sufficient.

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
```

### Agents (`assets/agents/<id>/content/AGENT.md`)

An agent asset is one markdown file with YAML frontmatter — the shared subset all
providers read:

```yaml
---
name: code-reviewer               # the agent's identity; defaults to the asset id
description: When to delegate to this agent.
# model: claude-haiku-4-5         # optional; provider-specific model ids
---
(system prompt / instructions as the markdown body)
```

Claude Code, Cursor, and Copilot receive the file as-is (Copilot under the
required `.agent.md` name); Codex receives a generated `.codex/agents/<id>.toml`
with `name`, `description`, `developer_instructions` (the body), and `model`.
Unknown frontmatter fields are ignored by the products that don't use them.

## Add an external asset

```bash
agentpack import https://github.com/anthropics/skills/tree/main/skills/pdf@<commit-sha>
```

- The `@<ref>` **must** be a full commit SHA or an immutable tag. Branches are rejected — you are approving exact content, not a moving target.
- Read the upstream content at that ref before opening the PR; the PR review approves that exact code.
- Record the upstream license with `--license MIT` when known; validation warns when missing.

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
```

On merge to main, run `agentpack catalog lock` and commit the refreshed `catalog.lock.yaml` (or require authors to run it locally — `--check` keeps them honest).

Recommended CODEOWNERS:

```text
assets/**            @org/ai-platform
assets/hooks/**      @org/security
```

Hooks execute code on developer machines and any external source change pulls third-party code — route both to security review.

## Team overlays

A consuming repo can add team-local assets without touching the org catalog:

```bash
agentpack init --overlay               # optional: `new --overlay` also creates it
agentpack new skills service-setup --overlay
```

This writes `.agentpack/catalog.yaml` and `.agentpack/assets/<kind>/<id>/` directly. Overlay entries with the same id override the org catalog for that repo. Without a configured base catalog, `.agentpack/catalog.yaml` works as a standalone personal-project catalog. See [team-overlays.md](team-overlays.md).
