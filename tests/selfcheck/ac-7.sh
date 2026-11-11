#!/usr/bin/env bash
# AC-7: after any run (pass, fail or kill) no selfcheck worktree or container is left behind.
. "$(dirname "$0")/lib.sh"; need_container AC-7
d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
fixture_repo "$d"; use_fixture_stages; export SELFCHECK_CMD_RESTORE=''
cd "$d"
bash scripts/selfcheck.sh > /dev/null 2>&1; no_leftovers "$d"; no_containers            # pass
touch broken-build; git add -A; git commit -qm break
bash scripts/selfcheck.sh > /dev/null 2>&1; no_leftovers "$d"; no_containers            # fail
export SELFCHECK_CMD_BUILD='sleep 120'                                                 # killed mid-stage
bash scripts/selfcheck.sh > /dev/null 2>&1 & pid=$!
for _ in $(seq 60); do [ -n "$("$ENGINE" ps -q --filter name=selfcheck-)" ] && break; sleep 0.5; done
sleep 1; kill -TERM "$pid"; wait "$pid" 2>/dev/null
no_leftovers "$d"; no_containers
echo "ok AC-7"
