# External Assets

External assets live in someone else's git repo (GitHub, Azure DevOps, any `.git` URL). The catalog stores only a manifest pointing at a **pinned, reviewed ref**.

## The contract

- **Pin or nothing.** `ref` must be a full commit SHA (preferred) or an immutable tag. Branches (`main`, `latest`, `refs/heads/...`) are rejected at validation. AgentPack never follows upstream automatically.
- **Checksummed.** `agentpack catalog lock` fetches the pinned content and records its sha256 in `catalog.lock.yaml`. Every later install verifies the hash — if upstream force-pushes over the SHA or a tag moves, the install fails loudly (exit 3).
- **Upgrades are PRs.** Bumping `ref` is a catalog change; the reviewer re-reads the upstream diff. That's the human gate the org wants for third-party code.

## Authoring

```bash
agentpack import https://github.com/anthropics/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d
```

External custom agents use the same verified pipeline:

```bash
agentpack import \
  https://github.com/github/awesome-copilot/blob/main/agents/dotnet-upgrade.agent.md@<commit-sha> \
  --kind agents --id dotnet-upgrade \
  --tool read --tool search --tool edit --tool execute --tool web \
  --instruction dotnet-conventions --skill dependency-analysis --mcp microsoft-docs
agentpack add dotnet-upgrade
```

The pinned upstream file supplies only the agent prompt body. AgentPack strips its YAML frontmatter before compilation. The interactive importer may show the upstream name, description, and tool aliases as untrusted authoring suggestions. Model metadata is always ignored, and tool permissions or dependencies become trusted only when explicitly declared in the AgentPack manifest; upstream skills, MCP servers, hooks, or nested agent declarations are never imported implicitly.

The catalog's `agent-governance-reviewer` is a concrete example. Its upstream `codebase` and `terminalCommand` tools become portable `read`, `search`, and `execute`. The importer reports that upstream `gpt-4o` will be removed; Claude, Codex, Copilot, Cursor, and Agentic Workflows use the model already selected by the user or workflow:

```bash
agentpack add agent-governance-reviewer --claude --codex --copilot --cursor
```

Claude and Copilot enforce the translated native allowlist. Codex and Cursor do not have an equivalent granular field; because this agent declares `execute`, they retain their writable parent tool surface and receive the declared capability policy in the composed prompt. Use a read/search-only overlay if your organization requires the reviewer to be mechanically read-only on every provider.

External hooks keep their executable content upstream, while the reviewed manifest owns the trigger and command contract:

```bash
agentpack import \
  https://github.com/acme/agent-assets/tree/main/hooks/secret-scan@<commit-sha> \
  --kind hooks --id secret-scan \
  --hook-trigger preToolUse --hook-tool Bash \
  --hook-command hook.sh --hook-timeout 30
```

The selected upstream directory must contain `hook.sh`. Absolute paths and commands that escape the fetched directory are rejected. A `notification` hook must restrict its providers because Codex and Cursor do not expose that trigger.

For MCP, prefer typed metadata and an explicit tool inventory:

```bash
agentpack import \
  https://github.com/acme/docs-mcp@<commit-sha> \
  --kind mcp --id docs \
  --mcp-server docs --mcp-transport http \
  --mcp-url https://docs.example.com/mcp \
  --mcp-tool search --mcp-tool fetch \
  --mcp-header-env Authorization=DOCS_TOKEN
```

For stdio use `--mcp-command`, repeatable `--mcp-arg`, and repeatable `--mcp-env NAME`. For HTTP/SSE use `--mcp-url` and `--mcp-header-env HEADER=NAME`. Values are never accepted. A direct external MCP may instead supply a root `mcp.json`, but an MCP imported by an agent must have typed manifest metadata with `mcp.tools` so dependency resolution never trusts a raw upstream inventory.

## Supported external kinds and content contracts

| Kind | Required pinned content | Provider behavior |
|---|---|---|
| agents | one Markdown file, preferably `AGENT.md` or `<id>.agent.md` | compiled to every declared native agent format; upstream frontmatter stripped |
| skills | directory with root `SKILL.md` | copied to each provider's native skill directory |
| hooks | directory containing the reviewed `hook.command` | content copied and provider hook configuration generated from typed metadata |
| mcp | typed manifest metadata, or a root `mcp.json` for direct installs | merged using provider-native MCP configuration |
| instructions | exactly one Markdown file | installed only on provider/scope combinations that support instructions |
| prompts | exactly one Markdown file | installed only on provider/scope combinations that support prompts |
| rules | exactly one `.mdc` file | Cursor only; other providers report an explicit skip |
| tools | unsupported | package custom tools as MCP |
| templates | unsupported | no provider-native installation contract |

`agentpack import` rejects `tools` and `templates` immediately. The other fetched shapes are validated whenever content enters or is reused from cache, including `catalog lock`, `catalog verify-external`, and installation.

Manifest (one line):

```yaml
source: https://github.com/anthropics/skills/tree/main/skills/pdf@9d2f1ae187231d8199c64b5b762e1bdf2244733d
```

With a recorded license, use the mapping form:

```yaml
source:
  url: https://github.com/anthropics/skills/tree/main/skills/pdf
  ref: 9d2f1ae187231d8199c64b5b762e1bdf2244733d
  license: MIT
```

Supported URL shapes:

| Shape | Example |
|---|---|
| GitHub tree/blob | `https://github.com/owner/repo/tree/main/path/inside` |
| GitHub repo | `https://github.com/owner/repo` (+ `path:` for a subfolder) |
| Azure DevOps | `https://dev.azure.com/org/project/_git/repo?path=/skills/x` |
| Any git remote | `https://host/whatever.git` (+ `path:`) |

Private repos work through your normal git credentials (credential helper / SSH) — agentpack shells out to `git`.

## How installs work

1. Clone/fetch into the content-addressed cache `~/.agentpack/cache/external/<key>/`.
2. Checkout the pinned ref, copy the subpath out, hash it.
3. Compare against `catalog.lock.yaml`; mismatch = hard failure, cache entry discarded.
4. Validate the kind-specific content contract and typed metadata.
5. Install like any local asset, or compile it to each provider's native agent format.

Planning (`agentpack plan`, `list`) never touches the network; fetching happens only on apply or `catalog verify-external`.

## CI verification

`agentpack catalog verify-external` fetches every external asset at its pinned ref and verifies checksums — run it on every catalog PR and nightly, so a moved tag or force-push is caught before a developer hits it.
