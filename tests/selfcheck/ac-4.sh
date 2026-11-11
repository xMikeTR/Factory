#!/usr/bin/env bash
# AC-4: with one fixture edited by hand, contracts fails and names the file. (Control: untouched, it passes.)
. "$(dirname "$0")/lib.sh"
d=$(mktemp -d); dc=$(mktemp -d); trap 'rm -rf "$d" "$dc"' EXIT
contracts_fixture "$d" "$dc"; use_real_contracts; cd "$d"
bash scripts/selfcheck.sh --local --designc "$dc" > out.json 2>/dev/null || fail "control run should pass"
echo edited > fx/b.json; git add -A; git commit -qm "hand edit"
bash scripts/selfcheck.sh --local --designc "$dc" > out.json 2>/dev/null; rc=$?
[ $rc -eq 1 ] || fail "expected exit 1, got $rc"
[ "$(json out.json "[s['outcome'] for s in d['stages'] if s['name']=='contracts']")" = "['fail']" ] || fail "contracts did not fail"
json out.json "[s['tail'] for s in d['stages'] if s['name']=='contracts'][0]" | grep -q "fx/b.json" || fail "the failing fixture is not named"
echo "ok AC-4"
