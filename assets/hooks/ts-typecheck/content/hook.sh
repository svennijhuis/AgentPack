#!/usr/bin/env bash
# Stop hook: once the agent finishes, typecheck the project and surface errors
# to stderr. Advisory only — always allows the stop. No-op without a TS project.
set -uo pipefail

cat >/dev/null 2>&1 || true   # drain stdin
ok() { echo '{"ok":true}'; exit 0; }

[ -f tsconfig.json ] || ok
command -v npx >/dev/null 2>&1 || ok

if [ -f package.json ] && grep -q '"typecheck"' package.json 2>/dev/null; then
  out="$(npm run --silent typecheck 2>&1)"; status=$?
else
  out="$(npx --no-install tsc --noEmit 2>&1)"; status=$?
fi

# Toolchain not actually present -> stay quiet.
if printf '%s' "$out" | grep -qiE 'not found|could not determine executable|need to install'; then ok; fi

if [ "$status" -ne 0 ]; then
  {
    echo "ts-typecheck: TypeScript errors detected —"
    printf '%s\n' "$out" | tail -n 30
  } >&2
fi
ok
