# Shared by the selfcheck acceptance tests. Each test makes a throwaway git repo that holds a copy of the script,
# and swaps the stage commands through the SELFCHECK_CMD_* seam so no .NET build is needed.
SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../scripts" && pwd)

fail() { echo "FAIL: $*" >&2; exit 1; }

# fixture_repo <dir>: a repo with one commit; `flag` holds "ok".
fixture_repo() {
  mkdir -p "$1/scripts" && cp "$SCRIPT_DIR/selfcheck.sh" "$1/scripts/"
  git -C "$1" init -q && git -C "$1" config user.email t@t && git -C "$1" config user.name t
  echo ok > "$1/flag"; git -C "$1" add -A && git -C "$1" commit -qm base
}

# Stage commands for the fixture: each passes while `flag` says ok and `broken-<stage>` does not exist.
use_fixture_stages() {
  export SELFCHECK_CMD_BUILD='grep -q ok flag && test ! -e broken-build'
  export SELFCHECK_CMD_TESTS_CORE='grep -q ok flag && test ! -e broken-tests-core'
  export SELFCHECK_CMD_TESTS_INTEGRATION='grep -q ok flag && test ! -e broken-tests-integration'
}

# json <file> <python expr over d>
json() { python3 -c "import json,sys; d=json.load(open('$1')); print($2)"; }

# No selfcheck worktree may outlive a run (AC-7 in the spec, cheap to hold from slice 1).
no_leftovers() { [ -z "$(git -C "$1" worktree list | grep -v "^$1 ")" ] || fail "a worktree was left behind in $1"; }
