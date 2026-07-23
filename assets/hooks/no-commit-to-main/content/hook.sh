#!/usr/bin/env bash
# no-commit-to-main: block `git commit` while the working tree is on a
# protected branch (main/master). Advises branching first.
set -u
payload="$(cat 2>/dev/null || true)"

printf '%s' "$payload" | grep -Eq 'git[[:space:]]+commit' || exit 0

branch="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo '')"
case "$branch" in
  main|master)
    printf 'BLOCKED: direct commits to %s are not permitted by policy. Create a branch first.\n' "$branch" >&2
    printf '{"permission":"deny","reason":"no-commit-to-main blocked a commit on %s"}\n' "$branch"
    exit 2 ;;
esac
exit 0
