# Agent Authoring

AgentPack keeps portable catalog metadata separate from the agent prompt. There is one source prompt, and the compiler generates the native Claude, Codex, Copilot, and Cursor files.

## The two author-owned files

The manifest is authoritative for identity, discovery, tools, and dependencies:

```yaml
# assets/agents/agent-governance-reviewer/agentpack.yaml
name: Agent Governance Reviewer
version: 1.0.0
description: >-
  Reviews agent systems for safety issues, missing governance controls,
  policy enforcement, trust scoring, and audit trails.
groups: [engineering, security]
providers: [claude, codex, copilot, cursor]
status: experimental
channel: internal

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

The prompt source is ordinary Markdown with no provider frontmatter:

```markdown
<!-- assets/agents/agent-governance-reviewer/content/AGENT.md -->
You are an expert in AI agent governance, safety, and trust systems.

## Your expertise

- Governance policy design
- Semantic intent classification
- Trust scoring with temporal decay
- Append-only audit trail design

## When reviewing code

1. Check whether tool functions enforce policy.
2. Check inputs before agent processing.
3. Find hardcoded credentials and secrets.
4. Verify audit logging and rate limits.
5. Verify trust boundaries between agents.
```

Do not repeat the YAML frontmatter four times. `description` is required at the top level of `agentpack.yaml`; compilation reports `[agent.description.missing]` when it is empty. The folder name supplies the stable kebab-case id used for native `name` fields. The top-level `name` is the human-facing catalog label.

If an imported GitHub agent already has frontmatter, AgentPack strips it. The interactive importer shows its name, description, and recognizable tools as untrusted suggestions. Models are always discarded, and upstream frontmatter cannot add dependencies, MCP servers, or trusted permissions.

## What `tools` means

`agent.tools` is a portable declaration of built-in capabilities. It does not install anything. Custom tools belong in a typed MCP asset under `agent.imports.mcp`.

| Portable capability | Claude output | Copilot output | Codex output | Cursor output |
|---|---|---|---|---|
| `read` | `Read` | `read` | inherited native file tools | inherited native file tools |
| `search` | `Glob`, `Grep` | `search` | inherited native search | inherited native search |
| `edit` | `Edit`, `Write` | `edit` | controls the coarse sandbox projection | controls the coarse `readonly` projection |
| `execute` | `Bash` | `execute` | inherited shell; controls sandbox projection | inherited shell; controls `readonly` projection |
| `web` | `WebFetch`, `WebSearch` | `web` | inherited `web_search` configuration | inherited web tools |
| `agent` | `Agent` | `agent` | inherited multi-agent tools | inherited subagent tools |

Claude and Copilot receive an exact native allowlist. Codex and Cursor do not expose an equivalent per-agent list, so the compiler uses their native coarse safety controls: an agent without both `edit` and `execute` becomes read-only. It also writes the portable capability policy into the composed prompt, but does not falsely claim that this mechanically removes omitted native tools. Imported MCP tool names are appended to the Claude and Copilot allowlists; Codex enables them in the agent-local MCP section; Cursor inherits them from `.cursor/mcp.json`.

GitHub's `web` alias is useful for IDE/CLI surfaces, but GitHub's current cloud coding agent does not implement that alias. The generated file remains portable across Copilot surfaces; compilation cannot turn cloud web access on where GitHub does not provide it.

Omit `tools` to inherit every tool the provider makes available. An empty list is rejected because it is too easy to confuse with inheritance.

## Model policy

AgentPack never writes a model into a generated agent. This keeps catalog agents usable with the model selected by each user, session, CLI, IDE, or Agentic Workflow. If an imported agent has `model` in its frontmatter, or an older manifest has `agent.models`, loading succeeds with `[agent.model.ignored]`, compilation removes the field, and the compatibility preview says `current/default`.

There is intentionally no `--model` authoring option. Delete legacy model mappings after seeing the warning; they have no effect on output or render fingerprints.

## Generated native files

The following excerpts show the shape of the generated files. Imported instruction text and required dependency notes are appended to every prompt body.

### Claude: `.claude/agents/agent-governance-reviewer.md`

<!-- agentpack-compile-example:claude -->

```markdown
---
name: "agent-governance-reviewer"
description: "Reviews agent systems for safety issues, missing governance controls, policy enforcement, trust scoring, and audit trails."
tools: ["Read", "Glob", "Grep", "Edit", "Write", "Bash", "WebFetch", "WebSearch", "mcp__microsoft.docs.mcp__microsoft_docs_search", "mcp__microsoft.docs.mcp__microsoft_docs_fetch", "mcp__microsoft.docs.mcp__microsoft_code_sample_search"]
skills: ["dependency-analysis"]
mcpServers: [{"microsoft.docs.mcp":{"type":"http","url":"https://learn.microsoft.com/api/mcp","tools":["microsoft_docs_search","microsoft_docs_fetch","microsoft_code_sample_search"]}}]
---

You are an expert in AI agent governance, safety, and trust systems.
```

### Codex: `.codex/agents/agent-governance-reviewer.toml`

<!-- agentpack-compile-example:codex -->

```toml
name = "agent-governance-reviewer"
description = "Reviews agent systems for safety issues, missing governance controls, policy enforcement, trust scoring, and audit trails."
developer_instructions = "You are an expert in AI agent governance, safety, and trust systems.\n\n## Required skills\nUse the installed `dependency-analysis` skill when its instructions apply.\n"

[mcp_servers."microsoft.docs.mcp"]
type = "http"
url = "https://learn.microsoft.com/api/mcp"
enabled_tools = ["microsoft_docs_search", "microsoft_docs_fetch", "microsoft_code_sample_search"]
```

Codex inherits the current session model; AgentPack does not generate a model setting.

### GitHub Copilot: `.github/agents/agent-governance-reviewer.agent.md`

<!-- agentpack-compile-example:copilot -->

```markdown
---
name: "agent-governance-reviewer"
description: "Reviews agent systems for safety issues, missing governance controls, policy enforcement, trust scoring, and audit trails."
tools: ["read", "search", "edit", "execute", "web", "microsoft.docs.mcp/microsoft_docs_search", "microsoft.docs.mcp/microsoft_docs_fetch", "microsoft.docs.mcp/microsoft_code_sample_search"]
mcp-servers: {"microsoft.docs.mcp":{"type":"http","url":"https://learn.microsoft.com/api/mcp","tools":["microsoft_docs_search","microsoft_docs_fetch","microsoft_code_sample_search"]}}
---

You are an expert in AI agent governance, safety, and trust systems.
```

GitHub Agentic Workflows can import the installed project file directly:

```yaml
imports:
  - .github/agents/agent-governance-reviewer.agent.md
```

### Cursor: `.cursor/agents/agent-governance-reviewer.md`

<!-- agentpack-compile-example:cursor -->

```markdown
---
name: "agent-governance-reviewer"
description: "Reviews agent systems for safety issues, missing governance controls, policy enforcement, trust scoring, and audit trails."
readonly: false
---

You are an expert in AI agent governance, safety, and trust systems.
```

Cursor subagents inherit the user's current model because no `model` field is generated. They inherit the imported MCP server from the separately merged `.cursor/mcp.json`. Cursor has no native granular `tools` frontmatter field, so none is invented.

## Compile before installation

```bash
agentpack catalog validate
agentpack catalog lock --check
agentpack catalog verify-external
agentpack catalog compile
```

`catalog compile` resolves every typed dependency, renders every targeted provider and scope, parses generated YAML/TOML, and fails before installation if an output is invalid.

## Native format references

- [Claude Code subagents](https://code.claude.com/docs/en/sub-agents)
- [Codex custom agents and subagents](https://learn.chatgpt.com/docs/agent-configuration/subagents)
- [GitHub Copilot custom agent configuration](https://docs.github.com/en/copilot/reference/custom-agents-configuration)
- [Cursor subagents](https://cursor.com/docs/subagents)
- [GitHub Agentic Workflow agent-file imports](https://github.github.com/gh-aw/reference/copilot-custom-agents/)
