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

- **CI on every PR:** `agentpack catalog validate` + `agentpack catalog lock --check` + `agentpack catalog verify-external` + `agentpack catalog compile`. A PR cannot merge with broken references, stale checksums, unpinned external refs, or invalid native agent output.

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
- Secrets never enter the catalog: MCP env vars are names, rendered as `${VAR}` placeholders at install.
- Bumping an external ref is a PR: third-party changes get re-reviewed by a human, every time.

## Ownership

`owner:` in a manifest is informational. The enforceable ownership mechanism is CODEOWNERS — prefer it.
