#!/usr/bin/env bash
# Stop hook: once the agent finishes, build the project and surface errors to
# stderr. Advisory only — always allows the stop. No-op without a project/SDK.
set -uo pipefail

cat >/dev/null 2>&1 || true   # drain stdin
ok() { echo '{"ok":true}'; exit 0; }

command -v dotnet >/dev/null 2>&1 || ok

# Only run inside a .NET project/solution (search a few levels down).
if ! ls ./*.sln ./*.slnx ./*.csproj >/dev/null 2>&1 \
   && [ -z "$(find . -maxdepth 3 \( -name '*.sln' -o -name '*.slnx' -o -name '*.csproj' \) 2>/dev/null | head -n1)" ]; then
  ok
fi

out="$(dotnet build --nologo -clp:ErrorsOnly 2>&1)"; status=$?
if [ "$status" -ne 0 ]; then
  {
    echo "dotnet-build-check: build errors detected —"
    printf '%s\n' "$out" | grep -Ei 'error' | head -n 30
  } >&2
fi
ok
