# Catalog Contributions

The normal contribution workflow is one command:

```bash
agentpack submit <kind> <path-or-url-or-id>
```

Examples:

```bash
agentpack submit skill ./my-skill
agentpack submit hook ./secret-scan --command scripts/check.sh --group security
agentpack submit mcp github --command github-mcp-server --env GITHUB_TOKEN
agentpack submit skill https://github.com/example/skills/tree/main/skills/code-review
```

Contributors do not edit manifests, checksums, lockfiles, or the catalog's default branch. AgentPack creates a proposal checkout and branch, generates the required metadata, validates it, commits it, pushes the branch, and opens a pull request.

## Local and external submissions

A local path copies its content into the catalog proposal:

```text
assets/<kind>/<id>/
  agentpack.yaml
  content/...
```

Local submissions are bounded and explicit. Before cloning the catalog, AgentPack shows every file it will include. It ignores common VCS, dependency, build, and cache folders; rejects symlinks, secret-like files, private keys, and oversized folders; and asks for confirmation before publishing. A skill folder must contain a top-level `SKILL.md`, which prevents accidentally submitting a whole repository or home folder.

Hooks accept either one script file or a folder. A single script is its inferred command. For a folder with multiple possible scripts, select the reviewed entry file explicitly:

```bash
agentpack submit hook ./check-secrets.sh --trigger preToolUse
agentpack submit hook ./secret-check --command scripts/check.sh --timeout 30
```

The hook command must be a relative file inside the submitted content. Paths outside the folder and symlinks are rejected.

Instructions, rules, prompts, and agents install as a single provider-native file, so submit one file for those kinds. Use a skill when the asset needs supporting scripts, references, or other package content. Generic tools and templates are rejected until AgentPack has a real provider-native destination for them.

MCP submissions do not copy an arbitrary local folder. They generate a typed, reviewable configuration:

```bash
agentpack submit mcp github \
  --command github-mcp-server \
  --arg stdio \
  --env GITHUB_TOKEN

agentpack submit mcp company-docs \
  --url https://mcp.example.com \
  --header-env Authorization=COMPANY_MCP_TOKEN
```

Use either `--command` (stdio) or `--url` (HTTP/SSE), never both. `--env` and `--header-env` accept environment-variable names only; secret values are rejected. Remote URLs require HTTPS, except localhost during development.

An external URL keeps the content in its original repository. AgentPack resolves the latest commit currently on the URL's branch and records that exact SHA:

```yaml
source:
  url: https://github.com/example/skills/tree/main/skills/code-review
  ref: 9d2f1ae187231d8199c64b5b762e1bdf2244733d
  license: MIT
```

Use `--ref <commit-or-tag>` to select a specific immutable revision. Use `--id` when the URL's final path segment is not the desired catalog ID, and `--version` when the new asset should not start at `1.0.0`.

## What `submit` does

1. Selects the active approved catalog.
2. Scans and previews the exact proposal.
3. Confirms before anything is published.
4. Clones the catalog's configured default branch.
5. Creates `agentpack/submit/<id>-<timestamp>`.
6. Copies reviewed local content or pins the external source.
7. Generates `agentpack.yaml` and `catalog.lock.yaml` in the background.
8. Validates every catalog asset and checksum.
9. Commits and pushes only the proposal branch—to the catalog for maintainers, or to the contributor's automatically managed fork when catalog access is read-only.
10. Opens a pull request using the GitHub CLI.

Use `--prepare-only` to stop after the local commit. Nothing is pushed in that mode. Contributors do not need write access to the official catalog; an authenticated GitHub CLI creates or reuses their fork and opens the pull request back to the official repository.

## Review checklist

Reviewers should confirm:

- the description says when the asset should be used;
- local content matches the stated purpose;
- external content is reviewed at the pinned commit;
- license and required attribution files are preserved;
- hooks and MCP commands are safe to execute;
- groups and provider restrictions are appropriate;
- CI passes `catalog validate`, `catalog lock --check`, and `catalog verify-external`.

## Repository enforcement

`submit` never targets `main`, but repository rules are the final enforcement boundary. Require pull requests, CODEOWNER approval, passing catalog checks, resolved conversations, and protection against force pushes. See [catalog repository setup](catalog-repository-setup.md).
