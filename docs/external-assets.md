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
4. Install like any local asset (copy tree / merge).

Planning (`agentpack plan`, `list`) never touches the network; fetching happens only on apply or `catalog verify-external`.

## CI verification

`agentpack catalog verify-external` fetches every external asset at its pinned ref and verifies checksums — run it on every catalog PR and nightly, so a moved tag or force-push is caught before a developer hits it.
