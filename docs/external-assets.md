# External Assets

External assets live in someone else's git repo (GitHub, Azure DevOps, any `.git` URL). The catalog stores only a manifest pointing at a **pinned, reviewed ref**.

## The contract

- **Pin or nothing.** `ref` must be a full commit SHA (preferred) or an immutable tag. Branches (`main`, `latest`, `refs/heads/...`) are rejected at validation. AgentPack never follows upstream automatically.
- **Checksummed.** `agentpack catalog lock` fetches the pinned content and records its sha256 in `catalog.lock.yaml`. Every later install verifies the hash — if upstream force-pushes over the SHA or a tag moves, the install fails loudly (exit 3).
- **Upgrades are PRs.** Bumping `ref` is a catalog change; the reviewer re-reads the upstream diff. That's the human gate the org wants for third-party code.
- **The repository is the attribution.** The source URL identifies the upstream project and the pinned ref identifies the exact reviewed revision.
- **License metadata stays lightweight.** Record the license when known. An omitted license is allowed with a warning; it is not treated as permission to redistribute.

## Authoring

```bash
agentpack import https://github.com/example-org/agent-assets/tree/main/skills/code-review@9d2f1ae187231d8199c64b5b762e1bdf2244733d
```

Pass `--license MIT` when the license is known. Otherwise import continues without another question.

Manifest:

```yaml
source:
  url: https://github.com/example-org/agent-assets/tree/main/skills/code-review
  ref: 9d2f1ae187231d8199c64b5b762e1bdf2244733d
  license: MIT
```

`license` should use the upstream SPDX identifier when available. If no license can be confirmed, omit the field; catalog validation warns instead of failing. Reviewers must still confirm that the license applies to the exact imported path and that any copyright, license, or NOTICE files required by it remain with the content; manifest metadata is not a substitute for those files. AgentPack preserves any recorded license with the source URL and ref in its installation lockfile.

When a consumer runs `agentpack add`, the install plan includes a **Source** column such as `example-org/agent-assets`. For a single external asset, the confirmation is explicit: `Add code-review from example-org/agent-assets?` Non-GitHub sources fall back to their repository URL.

Supported URL shapes:

| Shape | Example |
|---|---|
| GitHub tree/blob | `https://github.com/owner/repo/tree/main/path/inside` |
| GitHub repo | `https://github.com/owner/repo` (+ `path:` for a subfolder) |
| Azure DevOps | `https://dev.azure.com/org/project/_git/repo?path=/skills/x` |
| Any git remote | `https://host/whatever.git` (+ `path:`) |

Private repos work through your normal git credentials (credential helper / SSH) — agentpack shells out to `git`.

The subpath can be as deep as the repo nests it — catalog-style repos often organize skills by topic:

```bash
agentpack import https://github.com/mattpocock/skills/tree/main/skills/engineering/code-review@<commit-sha>
```

The asset id defaults to the last path segment (`code-review`); override with `--id`.

### Per-tool extras inside a skill (`agents/openai.yaml`, …)

Upstream skills may ship optional tool-specific files next to `SKILL.md` — for example Codex's `agents/openai.yaml` (ChatGPT desktop-app display name/icon, invocation policy, MCP tool dependencies). These are **not required**: every CLI discovers a skill from `SKILL.md` alone. agentpack copies the skill folder byte-for-byte, so such extras are preserved exactly as reviewed — and it never generates them, because inventing invocation policy or tool dependencies would change behavior the reviewer didn't approve.

## How installs work

1. Clone/fetch into the content-addressed cache `~/.agentpack/cache/external/<key>/`.
2. Checkout the pinned ref, copy the subpath out, hash it.
3. Compare against `catalog.lock.yaml`; mismatch = hard failure, cache entry discarded.
4. Install like any local asset (copy tree / merge).

Planning (`agentpack plan`, `list`) never touches the network; fetching happens only on apply or `catalog verify-external`.

## CI verification

`agentpack catalog verify-external` fetches every external asset at its pinned ref and verifies checksums — run it on every catalog PR and nightly, so a moved tag or force-push is caught before a developer hits it.
