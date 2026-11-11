#!/usr/bin/env bash
# Runs the selfcheck acceptance tests (tests/selfcheck/ac-*.sh). selfcheck does not call this on itself.
cd "$(dirname "$0")/../../tests/selfcheck" || exit 2
rc=0; for t in ac-*.sh; do bash "$t" || rc=1; done; exit $rc
