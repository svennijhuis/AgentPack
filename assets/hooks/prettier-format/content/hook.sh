#!/usr/bin/env bash
# PostToolUse: format the edited JS/TS/JSON/CSS/MD file with the project's
# Prettier. Degrades to a no-op if there's no file, wrong type, or no Prettier.
set -uo pipefail

payload="$(cat)"
ok() { echo '{"ok":true}'; exit 0; }

file="$(printf '%s' "$payload" \
  | grep -oE '"(file_?[Pp]ath|path)"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | head -n1 | sed -E 's/.*:[[:space:]]*"(.*)"$/\1/' || true)"
[ -z "$file" ] && ok
case "$file" in
  *.ts|*.tsx|*.js|*.jsx|*.mjs|*.cjs|*.json|*.css|*.md) ;;
  *) ok ;;
esac
[ -f "$file" ] || ok
command -v npx >/dev/null 2>&1 || ok

# --no-install => only runs if Prettier is a project dependency; never downloads.
npx --no-install prettier --write "$file" >/dev/null 2>&1 || true
ok
