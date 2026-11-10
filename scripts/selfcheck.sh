#!/usr/bin/env bash
# selfcheck: a local stand-in for CI. Spec: selfcheck-spec.md (slice 1 of 7).
#
# Checks what is COMMITTED: a clean `git worktree` of the commit, never the working copy.
# Runs build, tests-core and tests-integration; a failed stage does not stop the later ones.
# Prints one JSON record on stdout, progress on stderr. Calls no model.
#
# Slice 1 is host-only, so --local is required. The container and the restore window are slice 2.
#
#   scripts/selfcheck.sh --local                 check HEAD
#   scripts/selfcheck.sh --local --commit <sha>  check another commit
#   scripts/selfcheck.sh --local --stage <name>  one stage (build | tests-core | tests-integration)
#
# Exit codes: 0 pass, 1 a check failed, 2 could not run.
set -u

commit=HEAD; only=""; local_run=0
while [ $# -gt 0 ]; do
  case "$1" in
    --local)  local_run=1 ;;
    --commit) commit="${2:-}"; shift ;;
    --stage)  only="${2:-}"; shift ;;
    *) echo "selfcheck: unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done
[ "$local_run" = 1 ] || { echo "selfcheck: slice 1 runs on the host only; pass --local" >&2; exit 2; }

repo=$(git rev-parse --show-toplevel 2>/dev/null) || { echo "selfcheck: not inside a git repository" >&2; exit 2; }
sha=$(git -C "$repo" rev-parse --verify --quiet "$commit^{commit}") || { echo "selfcheck: no such commit: $commit" >&2; exit 2; }
command -v python3 >/dev/null || { echo "selfcheck: python3 is needed to write the JSON record" >&2; exit 2; }

# Stage table: name | command | timeout in seconds. build is first; a failed build marks every tests-* stage not_run.
# SELFCHECK_CMD_<NAME> (name upper-cased, - as _) replaces a command. It exists so tests/selfcheck can drive the
# script without a real build; nothing else sets it.
stages=(
  "build|dotnet build Factory.sln -warnaserror|900"
  "tests-core|dotnet run --project tests/Factory.Core.Tests|900"
  "tests-integration|dotnet test tests/Factory.Integration.Tests|1800"
)
if [ -n "$only" ]; then
  keep=()
  for s in "${stages[@]}"; do [ "${s%%|*}" = "$only" ] && keep+=("$s"); done
  [ ${#keep[@]} -gt 0 ] || { echo "selfcheck: no such stage: $only" >&2; exit 2; }
  stages=("${keep[@]}")
fi

# The run tests what is committed. Say so when the working copy differs.
if [ -n "$(git -C "$repo" status --porcelain 2>/dev/null)" ]; then
  echo "selfcheck: working copy has uncommitted changes; checking the commit ${sha:0:12}, not the working copy" >&2
fi

work=$(mktemp -d "${TMPDIR:-/tmp}/selfcheck.XXXXXX") || { echo "selfcheck: cannot make a temp dir" >&2; exit 2; }
tree="$work/${sha:0:12}"
cleanup() {
  git -C "$repo" worktree remove --force "$tree" >/dev/null 2>&1
  git -C "$repo" worktree prune >/dev/null 2>&1
  rm -rf "$work"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

git -C "$repo" worktree add -q --detach "$tree" "$sha" || { echo "selfcheck: could not create a worktree for ${sha:0:12}" >&2; exit 2; }

records="$work/records.jsonl"; : > "$records"
build_failed=0; any_fail=0

record() { # name outcome ms tail-file
  python3 - "$1" "$2" "$3" "$4" >> "$records" <<'PY'
import json, sys
name, outcome, ms, tailf = sys.argv[1:5]
tail = ""
if outcome in ("fail", "could_not_run"):
    with open(tailf, "rb") as f:
        tail = f.read().decode("utf-8", "replace")[-2000:]
print(json.dumps({"name": name, "outcome": outcome, "ms": int(ms), "tail": tail}))
PY
}

for s in "${stages[@]}"; do
  IFS='|' read -r name cmd secs <<<"$s"
  var="SELFCHECK_CMD_$(echo "$name" | tr 'a-z-' 'A-Z_')"; [ -n "${!var:-}" ] && cmd="${!var}"
  out="$work/$name.out"
  if [ "$build_failed" = 1 ] && [ "$name" != build ]; then
    echo "selfcheck: $name not run (build failed)" >&2; : > "$out"; record "$name" not_run 0 "$out"; continue
  fi
  echo "selfcheck: $name: $cmd" >&2
  t0=$(date +%s%3N)
  (cd "$tree" && timeout "$secs" bash -c "$cmd") > "$out" 2>&1; rc=$?
  ms=$(( $(date +%s%3N) - t0 ))
  if [ $rc -eq 0 ]; then outcome=pass; else
    outcome=fail; any_fail=1; [ "$name" = build ] && build_failed=1
    [ $rc -eq 124 ] && echo "selfcheck: $name timed out after ${secs}s" >> "$out"
  fi
  echo "selfcheck: $name: $outcome (${ms} ms)" >&2
  record "$name" "$outcome" "$ms" "$out"
done

# --stage runs are for iteration and never count as a pass (the record, and the gate in a later slice, say so).
python3 - "$records" "$sha" "$any_fail" "${only:-}" <<'PY'
import json, sys, datetime
records, sha, any_fail, only = sys.argv[1:5]
stages = [json.loads(l) for l in open(records)]
print(json.dumps({
    "schema": "selfcheck/1", "commit": sha,
    "outcome": "fail" if any_fail == "1" else "pass",
    "env": "local", "partial": bool(only),
    "stages": stages,
    "at": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}, indent=2))
PY
[ "$any_fail" = 0 ] && exit 0; exit 1
