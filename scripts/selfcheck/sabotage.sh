#!/usr/bin/env bash
# selfcheck `sabotage` stage: the sabotage tests, reported on their own. A filter that matches no test is a FAIL,
# so deleting the sabotage cases cannot make this stage quietly pass. Extra arguments go to `dotnet test`.
set -u
o=$(dotnet test tests/Factory.Integration.Tests --filter Sabotage "$@" 2>&1); rc=$?
printf '%s\n' "$o"
[ $rc -eq 0 ] || exit $rc
printf '%s' "$o" | grep -Eq 'Total: +[1-9]' || { echo "selfcheck: no sabotage test matched the filter"; exit 1; }
