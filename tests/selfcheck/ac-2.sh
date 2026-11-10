#!/usr/bin/env bash
# AC-2: with an uncommitted breaking change in the working copy, the run still passes and prints the dirty-copy line.
. "$(dirname "$0")/lib.sh"
d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
fixture_repo "$d"; use_fixture_stages
echo broken > "$d/flag"                       # tracked file edited, not committed
cd "$d" && bash scripts/selfcheck.sh --local > out.json 2> err.txt; rc=$?
[ $rc -eq 0 ] || fail "expected exit 0, got $rc"
[ "$(json out.json "d['outcome']")" = pass ] || fail "record outcome is not pass"
grep -q "uncommitted changes" err.txt || fail "no dirty-copy line on stderr"
no_leftovers "$d"
echo "ok AC-2"
