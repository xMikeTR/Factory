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
  export SELFCHECK_CMD_CONTRACTS=true SELFCHECK_CMD_SABOTAGE=true SELFCHECK_CMD_DOCS=true
  export SELFCHECK_CMD_TESTS_INTEGRATION='grep -q ok flag && test ! -e broken-tests-integration'
}

# json <file> <python expr over d>
json() { python3 -c "import json,sys; d=json.load(open('$1')); print($2)"; }

# No selfcheck worktree may outlive a run (AC-7 in the spec, cheap to hold from slice 1).
no_leftovers() { [ -z "$(git -C "$1" worktree list | grep -v "^$1 ")" ] || fail "a worktree was left behind in $1"; }

# Container tests need the engine and the runner image; without them they report SKIP (exit 0), never a false pass.
ENGINE="${FACTORY_CONTAINER_ENGINE:-podman}"; IMAGE="${FACTORY_RUNNER_IMAGE:-factory-s2-runner:latest}"
need_container() {
  command -v "$ENGINE" >/dev/null && "$ENGINE" image inspect "$IMAGE" >/dev/null 2>&1 || { echo "SKIP $1: no '$ENGINE' or no image $IMAGE"; exit 0; }
}
no_containers() { [ -z "$("$ENGINE" ps -aq --filter name=selfcheck-)" ] || fail "a selfcheck container was left behind"; }

# contracts_fixture <dir> <designc-dir>: <dir> holds a fixture copy pinned to a fake designc repo (two source files).
contracts_fixture() {
  local d="$1" dc="$2"
  mkdir -p "$dc/docs" && git -C "$dc" init -q && git -C "$dc" config user.email t@t && git -C "$dc" config user.name t
  echo one > "$dc/docs/a.json"; echo two > "$dc/docs/b.json"; git -C "$dc" add -A; git -C "$dc" commit -qm "a b"
  fixture_repo "$d"; mkdir -p "$d/scripts/selfcheck" "$d/fx"
  cp "$SCRIPT_DIR/selfcheck/contracts.sh" "$d/scripts/selfcheck/"
  cp "$dc/docs/a.json" "$d/fx/a.json"; cp "$dc/docs/b.json" "$d/fx/b.json"
  printf '{"designc_commit":"%s","files":[{"fixture":"fx/a.json","source":"docs/a.json"},{"fixture":"fx/b.json","source":"docs/b.json"}]}\n' \
    "$(git -C "$dc" rev-parse HEAD)" > "$d/contracts.lock.json"
  git -C "$d" add -A; git -C "$d" commit -qm contracts
}
# Same stage seams as use_fixture_stages, but contracts runs for real.
use_real_contracts() { use_fixture_stages; unset SELFCHECK_CMD_CONTRACTS; }
