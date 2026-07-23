#!/usr/bin/env bash
# protect-secret-files: block writes/edits that target likely secret files
# (.env, private keys, credential stores) before they happen.
set -u

payload="$(cat 2>/dev/null || true)"

extract_path() {
  if command -v jq >/dev/null 2>&1; then
    printf '%s' "$payload" | jq -r '
      (.tool_input.file_path // .tool_input.path //
       .toolInput.file_path // .toolInput.path //
       .toolInput // empty)' 2>/dev/null | head -1
  fi
}

file="$(extract_path)"
if [ -z "${file:-}" ] || [ "$file" = "null" ]; then
  file="$(printf '%s' "$payload" | grep -oE '"(file_?[Pp]ath|path)"[[:space:]]*:[[:space:]]*"[^"]+"' \
        | head -1 | sed -E 's/.*:[[:space:]]*"//; s/"$//')"
fi

[ -n "${file:-}" ] || exit 0

base="$(basename "$file")"
case "$base" in
  .env|.env.*|*.pem|*.key|id_rsa|id_rsa.*|id_ed25519|id_ed25519.*|*.p12|*.pfx|credentials|.npmrc|.pypirc)
    printf 'BLOCKED: writing to a likely secret file (%s) is not permitted by policy.\n' "$file" >&2
    printf '{"permission":"deny","reason":"protect-secret-files blocked %s"}\n' "$file"
    exit 2 ;;
esac

exit 0
