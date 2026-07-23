#!/usr/bin/env bash
# format-on-write: after the agent edits a file, run the repo's own formatter on
# just that file. Detects the formatter from config files in the working tree.
# Never blocks the agent: every path exits 0.
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
# Fallback: first path-like token in the payload.
if [ -z "${file:-}" ] || [ "$file" = "null" ]; then
  file="$(printf '%s' "$payload" | grep -oE '"(file_?[Pp]ath|path)"[[:space:]]*:[[:space:]]*"[^"]+"' \
        | head -1 | sed -E 's/.*:[[:space:]]*"//; s/"$//')"
fi

[ -n "${file:-}" ] && [ -f "$file" ] || exit 0

run() { command -v "$1" >/dev/null 2>&1 && "$@" >/dev/null 2>&1 || true; }

case "$file" in
  *.ts|*.tsx|*.js|*.jsx|*.mjs|*.cjs|*.json|*.css|*.scss|*.md|*.mdx|*.html|*.yaml|*.yml)
    if [ -f .prettierrc ] || [ -f .prettierrc.json ] || [ -f .prettierrc.js ] || [ -f prettier.config.js ]; then
      run npx --no-install prettier --write "$file"
    fi ;;
  *.py)
    if [ -f pyproject.toml ] && grep -q 'ruff' pyproject.toml 2>/dev/null; then
      run ruff format "$file"
    elif command -v black >/dev/null 2>&1; then
      run black "$file"
    fi ;;
  *.go)
    run gofmt -w "$file" ;;
  *.cs)
    # dotnet format works on a project, not a single file; only run when cheap.
    if ls ./*.csproj >/dev/null 2>&1 || ls ./*.sln >/dev/null 2>&1; then
      run dotnet format --include "$file"
    fi ;;
esac

exit 0
