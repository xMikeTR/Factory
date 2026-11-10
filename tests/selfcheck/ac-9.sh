#!/usr/bin/env bash
# AC-9: no stage after restore can reach the network. A stage that tries to connect out fails to connect.
. "$(dirname "$0")/lib.sh"; need_container AC-9
d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
fixture_repo "$d"; use_fixture_stages
export SELFCHECK_CMD_RESTORE=''                                    # no restore: the fixture has no solution
export SELFCHECK_CMD_TESTS_CORE="timeout 5 bash -c 'exec 3<>/dev/tcp/1.1.1.1/443'"
cd "$d" && bash scripts/selfcheck.sh > out.json 2> err.txt; rc=$?
[ $rc -eq 1 ] || fail "expected exit 1 (the connect must fail), got $rc"
[ "$(json out.json "d['env']")" = container ] || fail "record env is not container"
[ "$(json out.json "[s['outcome'] for s in d['stages']]")" = "['pass', 'fail', 'pass', 'pass', 'pass', 'pass']" ] || fail "expected build pass, the network stage fail, integration pass: $(json out.json "[s['outcome'] for s in d['stages']]")"
no_leftovers "$d"; no_containers
echo "ok AC-9"
