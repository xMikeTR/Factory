#!/usr/bin/env bash
# AC-1: on a commit where every suite passes, selfcheck exits 0 and --status exits 0.
# Also: runs are appended as notes, --status exits 3 with no record, and --stage / --local passes never count.
. "$(dirname "$0")/lib.sh"
d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
fixture_repo "$d"; use_fixture_stages; export SELFCHECK_CMD_RESTORE=''
cd "$d"
base=$(git rev-parse HEAD)

bash scripts/selfcheck.sh --status > /dev/null 2>&1; rc=$?
[ $rc -eq 3 ] || fail "--status with no record: expected exit 3, got $rc"

# A local run is recorded but is not a pass for the gate.
bash scripts/selfcheck.sh --local > /dev/null 2>&1 || fail "local run should pass"
bash scripts/selfcheck.sh --status > st.json 2>/dev/null; rc=$?
[ $rc -eq 1 ] || fail "--status after a --local pass: expected exit 1, got $rc"
[ "$(json st.json "d['env']")" = local ] || fail "the note does not say env local"
[ "$(json st.json "len(d['checks_hash'])")" = 64 ] || fail "checks_hash missing"
[ "$(json st.json "d['by']")" = t ] || fail "by is not git user.name"

# Notes are appended: a second run adds a line, and --status reads the last.
bash scripts/selfcheck.sh --local --stage build > /dev/null 2>&1
[ "$(git notes --ref=selfcheck show "$base" | grep -c .)" = 2 ] || fail "second run did not append a note line"
bash scripts/selfcheck.sh --status > st.json 2>/dev/null
[ "$(json st.json "d['partial']")" = True ] || fail "--status did not read the last record"

# A failing commit: --status exits 1 and says fail. Other commits are untouched.
touch broken-build; git add -A; git commit -qm break; bad=$(git rev-parse HEAD)
bash scripts/selfcheck.sh --local > /dev/null 2>&1
bash scripts/selfcheck.sh --status "$bad" > st.json 2>/dev/null; rc=$?
[ $rc -eq 1 ] && [ "$(json st.json "d['outcome']")" = fail ] || fail "--status of a failing commit"

# The container run is the one that counts.
command -v "$ENGINE" >/dev/null && "$ENGINE" image inspect "$IMAGE" >/dev/null 2>&1 || { echo "SKIP AC-1 (container part): no '$ENGINE' or no image $IMAGE"; exit 0; }
git rm -q broken-build; git commit -qm fix; good=$(git rev-parse HEAD)
bash scripts/selfcheck.sh > /dev/null 2>&1 || fail "container run should pass"
bash scripts/selfcheck.sh --status "$good" > st.json 2>/dev/null || fail "--status should exit 0 after a container pass"
[ "$(json st.json "d['env']")" = container ] || fail "env is not container"
echo "ok AC-1"
