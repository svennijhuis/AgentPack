#!/usr/bin/env bash
# PostToolUse: run dotnet format on the edited C# file, scoped to its nearest
# project/solution. Degrades to a no-op without a .cs file or the .NET SDK.
set -uo pipefail

payload="$(cat)"
ok() { echo '{"ok":true}'; exit 0; }

file="$(printf '%s' "$payload" \
  | grep -oE '"(file_?[Pp]ath|path)"[[:space:]]*:[[:space:]]*"[^"]*"' \
  | head -n1 | sed -E 's/.*:[[:space:]]*"(.*)"$/\1/' || true)"
[ -z "$file" ] && ok
case "$file" in *.cs) ;; *) ok ;; esac
[ -f "$file" ] || ok
command -v dotnet >/dev/null 2>&1 || ok

# Walk up to the nearest directory holding a .csproj or .sln.
dir="$(cd "$(dirname "$file")" 2>/dev/null && pwd || true)"
proj=""
while [ -n "$dir" ] && [ "$dir" != "/" ]; do
  if ls "$dir"/*.csproj "$dir"/*.sln "$dir"/*.slnx >/dev/null 2>&1; then proj="$dir"; break; fi
  dir="$(dirname "$dir")"
done
[ -z "$proj" ] && ok

( cd "$proj" && dotnet format --include "$file" >/dev/null 2>&1 ) || true
ok
