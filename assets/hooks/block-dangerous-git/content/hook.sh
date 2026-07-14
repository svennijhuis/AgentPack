#!/usr/bin/env bash
# PreToolUse guard: deny destructive git commands. Adapted from mattpocock/skills
# git-guardrails. Reads the tool payload on stdin; emits a deny verdict + exit 2
# to block, or {"ok":true} to allow.
set -uo pipefail

payload="$(cat)"

# Pull the command string out of the tool input; fall back to the whole payload.
cmd="$(printf '%s' "$payload" \
  | grep -oE '"command"[[:space:]]*:[[:space:]]*"([^"\\]|\\.)*"' \
  | head -n1 | sed -E 's/.*"command"[[:space:]]*:[[:space:]]*"(.*)"$/\1/' || true)"
[ -z "$cmd" ] && cmd="$payload"

deny() {
  # Escape backslashes and quotes for JSON.
  esc="${1//\\/\\\\}"; esc="${esc//\"/\\\"}"
  printf '{"permission":"deny","reason":"%s"}\n' "$esc"
  exit 2
}

if printf '%s' "$cmd" | grep -Eq 'git[[:space:]]+push([[:space:]].*)?[[:space:]](-f([[:space:]]|$)|--force([[:space:]]|$)|--force-with-lease)'; then
  deny "Blocked: force push. Push without --force, or rebase and open a PR."
fi
if printf '%s' "$cmd" | grep -Eq 'git[[:space:]]+reset[[:space:]].*--hard'; then
  deny "Blocked: git reset --hard discards work. Stash or commit first."
fi
if printf '%s' "$cmd" | grep -Eq 'git[[:space:]]+clean[[:space:]].*-[[:alpha:]]*f'; then
  deny "Blocked: git clean -f deletes untracked files irreversibly."
fi
if printf '%s' "$cmd" | grep -Eq 'git[[:space:]]+branch[[:space:]].*-D'; then
  deny "Blocked: git branch -D force-deletes a branch. Use -d, or confirm it is merged."
fi
if printf '%s' "$cmd" | grep -Eq 'git[[:space:]]+checkout[[:space:]]+(--[[:space:]]|\.([[:space:]]|$))'; then
  deny "Blocked: this checkout can overwrite local changes. Stash first."
fi
if printf '%s' "$cmd" | grep -Eq 'git[[:space:]]+restore[[:space:]]' \
   && ! printf '%s' "$cmd" | grep -Eq -- '--staged'; then
  deny "Blocked: git restore discards worktree changes. Stash first, or use --staged."
fi

echo '{"ok":true}'
