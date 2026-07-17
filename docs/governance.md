# Governance

The org catalog is the trusted source of truth. Everything below assumes the catalog lives in one git repository with branch protection.

## The review gate

- **All changes are PRs.** The CLI (`agentpack new`, `agentpack import`) scaffolds; a human approves; CI validates. Nothing installs on developer machines that didn't pass this gate.
- **CODEOWNERS routes review:**

  ```text
  assets/**            @org/ai-platform
  assets/hooks/**      @org/security
  ```

  Hooks execute code on developer machines; external source changes pull third-party code. Both go to security.

- **CI on every PR:** `agentpack catalog validate` + `agentpack catalog lock --check` + `agentpack catalog verify-external`. A PR cannot merge with broken references, stale checksums, or unpinned external refs.

## Asset lifecycle

| Status | Meaning | Install behavior |
|---|---|---|
| `experimental` | new, opt-in | installs normally |
| `recommended` | the default choice | installs normally |
| `deprecated` | being retired | installs with a warning |
| `blocked` | banned (security/compliance) | refuses; explicit request is an error |

- Version bumps (semver) on every content change; consumers pick them up via `agentpack outdated` / `upgrade`.
- Group retirement: `status: deprecated` + `replacedBy` + `removeAfter` — enforced by validation.
- Emergency kill switch: set `status: blocked` and merge; installs stop immediately, `upgrade` warns existing users.

## Supply-chain posture

- External refs are pinned to commit SHAs (tags allowed but warned); branches are validation errors.
- `catalog.lock.yaml` records content hashes; installs verify them — a moved tag or force-push fails loudly.
  An external asset with no checksum at all warns when SHA-pinned and **fails validation when tag-pinned**,
  because a moved tag with no checksum would be undetectable.
- Secrets never enter the catalog: MCP env vars are names, rendered as `${VAR}` placeholders at install —
  enforced on typed `mcp:` metadata and on raw `content/mcp.json` files alike.
- Bumping an external ref is a PR: third-party changes get re-reviewed by a human, every time.

## Trust and failure model (read this before adopting)

- **A catalog source is code execution.** Hooks are scripts the AI tools run on every matching event, and
  MCP entries are command lines the tools launch. agentpack validates and pins content, but it does not
  sandbox it: anyone who can merge to a registered catalog can run code on every consuming machine. Guard
  the catalog repo (branch protection, CODEOWNERS above) with the same rigor as a deploy pipeline.
- **Merges are transactional per file; tree copies are not.** Shared config files (settings.json,
  mcp.json, config.toml) are written atomically. Copy-tree installs (skills, instructions, prompts,
  rules) copy file-by-file: a crash mid-install can leave a partial tree, but the previous content is
  always in `.agentpack/backups/` first, and a rerun repairs the install. A failure mid-batch records
  the items that did apply in the lockfile, so rerunning continues instead of double-applying.

## Ownership

`owner:` in a manifest is informational. The enforceable ownership mechanism is CODEOWNERS — prefer it.
