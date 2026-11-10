#!/usr/bin/env bash
# AC-3: with a committed failing check, the run exits 1, the record names the stage, and later stages still ran.
. "$(dirname "$0")/lib.sh"
d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
fixture_repo "$d"; use_fixture_stages
touch "$d/broken-tests-core"; git -C "$d" add -A; git -C "$d" commit -qm "break tests-core"
cd "$d" && bash scripts/selfcheck.sh --local > out.json 2> err.txt; rc=$?
[ $rc -eq 1 ] || fail "expected exit 1, got $rc"
[ "$(json out.json "d['outcome']")" = fail ] || fail "record outcome is not fail"
[ "$(json out.json "[s['name'] for s in d['stages'] if s['outcome']=='fail']")" = "['tests-core']" ] || fail "record does not name tests-core as the failed stage"
[ "$(json out.json "[s['outcome'] for s in d['stages'] if s['name']=='tests-integration']")" = "['pass']" ] || fail "tests-integration did not run after the failure"
no_leftovers "$d"

# A failed build marks the tests as not run rather than running them against nothing.
rm -f "$d/broken-tests-core"; touch "$d/broken-build"; git -C "$d" add -A; git -C "$d" commit -qm "break build"
bash scripts/selfcheck.sh --local > out2.json 2> err2.txt; rc=$?
[ $rc -eq 1 ] || fail "expected exit 1 for a broken build, got $rc"
[ "$(json out2.json "[s['outcome'] for s in d['stages']]")" = "['fail', 'not_run', 'not_run', 'not_run', 'pass', 'pass']" ] || fail "tests-* were not marked not_run after a build failure"
echo "ok AC-3"
