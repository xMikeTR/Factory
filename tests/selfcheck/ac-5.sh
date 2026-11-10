#!/usr/bin/env bash
# AC-5: with designc HEAD ahead on a source file, the run passes with a warning; with --strict-contracts it fails.
. "$(dirname "$0")/lib.sh"
d=$(mktemp -d); dc=$(mktemp -d); trap 'rm -rf "$d" "$dc"' EXIT
contracts_fixture "$d" "$dc"; use_real_contracts; cd "$d"
echo changed > "$dc/docs/a.json"; git -C "$dc" commit -qam "designc moves a"
bash scripts/selfcheck.sh --local --designc "$dc" > out.json 2>/dev/null || fail "lenient run should pass"
json out.json "d['warnings'][0]" | grep -q "a.json is behind designc HEAD (1 commit)" || fail "no warning naming a.json: $(json out.json "d['warnings']")"
[ "$(json out.json "d['designc_head'] != d['designc_commit']")" = True ] || fail "designc_head should differ from the pinned commit"
bash scripts/selfcheck.sh --local --designc "$dc" --strict-contracts > out.json 2>/dev/null; rc=$?
[ $rc -eq 1 ] || fail "--strict-contracts: expected exit 1, got $rc"
echo "ok AC-5"
