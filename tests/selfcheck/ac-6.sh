#!/usr/bin/env bash
# AC-6: with no designc checkout, the outcome is could_not_run and the exit code is 2; the other stages still ran.
. "$(dirname "$0")/lib.sh"
d=$(mktemp -d); dc=$(mktemp -d); trap 'rm -rf "$d" "$dc"' EXIT
contracts_fixture "$d" "$dc"; use_real_contracts; cd "$d"
bash scripts/selfcheck.sh --local --designc "$d/nowhere" > out.json 2>/dev/null; rc=$?
[ $rc -eq 2 ] || fail "expected exit 2, got $rc"
[ "$(json out.json "d['outcome']")" = could_not_run ] || fail "outcome is not could_not_run"
[ "$(json out.json "[s['outcome'] for s in d['stages']]")" = "['pass', 'pass', 'pass', 'could_not_run']" ] || fail "stages: $(json out.json "[s['outcome'] for s in d['stages']]")"
# --update re-copies from designc HEAD and re-pins; the next run passes with no warning.
echo changed > "$dc/docs/a.json"; git -C "$dc" commit -qam "designc moves a"
bash scripts/selfcheck/contracts.sh --designc "$dc" --update > /dev/null || fail "--update failed"
[ "$(cat fx/a.json)" = changed ] || fail "--update did not re-copy the fixture"
git add -A; git commit -qm update
bash scripts/selfcheck.sh --local --designc "$dc" > out.json 2>/dev/null || fail "run after --update should pass"
[ "$(json out.json "d['warnings']")" = "[]" ] || fail "warnings remain after --update"
echo "ok AC-6"
