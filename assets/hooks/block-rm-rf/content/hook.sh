#!/usr/bin/env bash
# block-rm-rf: block catastrophic recursive deletes before the agent runs them.
# Greps the raw tool payload, so it works regardless of provider payload shape.
set -u
payload="$(cat 2>/dev/null || true)"

# Recursive-force rm targeting root, home, or a bare wildcard/dot.
if printf '%s' "$payload" | grep -Eq 'rm[[:space:]]+(-[a-zA-Z]*[rf][a-zA-Z]*[[:space:]]+)+(-[a-zA-Z]*[[:space:]]+)*(/|~|\*|\.|/\*|\$HOME)([[:space:]]|"|'\''|$)'; then
  printf 'BLOCKED: a recursive force-delete of a root/home/wildcard path is not permitted by policy.\n' >&2
  printf '{"permission":"deny","reason":"block-rm-rf blocked a dangerous rm"}\n'
  exit 2
fi
exit 0
