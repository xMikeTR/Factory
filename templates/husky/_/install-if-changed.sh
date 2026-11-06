#!/bin/sh
# Shared by post-checkout, post-merge and post-rewrite.
# Usage: install-if-changed.sh <old-ref> <new-ref>
#
# If package.json or package-lock.json changed between the two refs, run `npm i` so node_modules matches the tree.
# Lockfile guard: if package.json did NOT change but `npm i` rewrote package-lock.json, that is drift. Put the lockfile
# back and fail loudly, rather than leaving an uncommitted change nobody asked for.
set -eu

old="$1"; new="$2"
[ -f package.json ] || exit 0

changed=$(git diff --name-only "$old" "$new" -- package.json package-lock.json 2>/dev/null || true)
[ -n "$changed" ] || exit 0

manifest_changed=no
case "$changed" in *package.json*) manifest_changed=yes ;; esac

backup=$(mktemp)
trap 'rm -f "$backup"' EXIT
[ -f package-lock.json ] && cp package-lock.json "$backup" || : > "$backup"

npm i

if [ "$manifest_changed" = no ] && ! cmp -s "$backup" package-lock.json 2>/dev/null; then
  if [ -s "$backup" ]; then cp "$backup" package-lock.json; else rm -f package-lock.json; fi
  echo "husky: 'npm i' changed package-lock.json but package.json did not change." >&2
  echo "husky: the lockfile was restored. Fix the drift (run 'npm i' on a clean tree and commit the lockfile) before continuing." >&2
  exit 1
fi
