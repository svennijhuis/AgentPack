#!/usr/bin/env bash
set -euo pipefail

payload="$(cat)"
if printf '%s' "$payload" | grep -Eiq '(api[_-]?key|secret|password)[=:][[:space:]]*[^[:space:]]+'; then
  echo '{"permission":"deny","reason":"Possible secret detected in tool input."}'
  exit 2
fi

echo '{"ok":true}'
