#!/usr/bin/env bash
# selfcheck: a local stand-in for CI. Spec: selfcheck-spec.md (slice 1 of 7).
#
# Checks what is COMMITTED: a clean `git worktree` of the commit, never the working copy.
# Runs build, tests-core and tests-integration; a failed stage does not stop the later ones.
# Prints one JSON record on stdout, progress on stderr. Calls no model.
#
# Default: the factory-s2-runner image. One `dotnet restore` runs in a container on the bridge (the only moment
# with network, nuget.org only, same gate as the factory's sandbox); every stage then runs in a `--network none`
# container. --local runs on the host instead and is recorded as env "local".
#
#   scripts/selfcheck.sh                 check HEAD in the container
#   scripts/selfcheck.sh --commit <sha>  check another commit
#   scripts/selfcheck.sh --stage <name>  one stage (build | tests-core | tests-integration)
#   scripts/selfcheck.sh --local         on the host, no container
#
# Environment: FACTORY_CONTAINER_ENGINE (podman by default), FACTORY_RUNNER_IMAGE (factory-s2-runner:latest).
# Exit codes: 0 pass, 1 a check failed, 2 could not run (no engine, no image, restore failed, bad arguments).
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
engine="${FACTORY_CONTAINER_ENGINE:-podman}"; image="${FACTORY_RUNNER_IMAGE:-factory-s2-runner:latest}"
image_id=""
if [ "$local_run" = 0 ]; then
  command -v "$engine" >/dev/null || { echo "selfcheck: container engine '$engine' not found (set FACTORY_CONTAINER_ENGINE, or use --local)" >&2; exit 2; }
  image_id=$("$engine" image inspect --format '{{.Id}}' "$image" 2>/dev/null) \
    || { echo "selfcheck: image $image is missing; build it with: FACTORY_CONTAINER_ENGINE=$engine docker/runner/build.sh" >&2; exit 2; }
fi

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

nuget_cache="${HOME}/.cache/factory-s2/nuget"; mkdir -p "$nuget_cache"
cname="selfcheck-$$-$(date +%s)"
work=$(mktemp -d "${TMPDIR:-/tmp}/selfcheck.XXXXXX") || { echo "selfcheck: cannot make a temp dir" >&2; exit 2; }
tree="$work/${sha:0:12}"
cleanup() {
  [ "$local_run" = 0 ] && "$engine" rm -f "$cname" "$cname-restore" >/dev/null 2>&1
  git -C "$repo" worktree remove --force "$tree" >/dev/null 2>&1
  git -C "$repo" worktree prune >/dev/null 2>&1
  rm -rf "$work"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

git -C "$repo" worktree add -q --detach "$tree" "$sha" || { echo "selfcheck: could not create a worktree for ${sha:0:12}" >&2; exit 2; }

records="$work/records.jsonl"; : > "$records"
build_failed=0; any_fail=0; restore_failed=0

# Same limits, user and mounts as the factory's own sandbox (DockerSandbox.LimitArgs / BaseEnv).
user="$(id -u):$(id -g)"
cargs=(--cap-drop ALL --security-opt no-new-privileges --user "$user" --tmpfs /tmp:exec,mode=1777
       -v "$tree:$tree" -v "$nuget_cache:/nuget" -w "$tree"
       -e HOME=/tmp/home -e DOTNET_CLI_HOME=/tmp/home -e NUGET_PACKAGES=/nuget -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1)
if [ "$local_run" = 0 ]; then
  "$engine" run -d --rm --name "$cname" --network none "${cargs[@]}" "$image" sleep infinity >/dev/null \
    || { echo "selfcheck: could not start the container from $image" >&2; exit 2; }
fi

# run_in <network-phase> <secs> <cmd>: runs on the host (--local) or in the container; output to stdout/stderr.
run_in() {
  if [ "$local_run" = 1 ]; then (cd "$tree" && timeout "$2" bash -c "$3"); return; fi
  if [ "$1" = restore ]; then
    # The one window with network: its own short-lived container on the bridge. The stage container never leaves `none`.
    "$engine" run --rm --name "$cname-restore" --network bridge "${cargs[@]}" "$image" timeout -k 5 "$2" bash -c "$3"
  else
    "$engine" exec -w "$tree" "$cname" timeout -k 5 "$2" bash -c "$3"
  fi
}

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

# Restore, then no network. Nothing is run if it fails: there is nothing to check against.
restore_cmd='dotnet restore Factory.sln --configfile /etc/factory/nuget.config -p:RestoreSources=https://api.nuget.org/v3/index.json -p:RestoreAdditionalProjectSources= -p:ImportProjectExtensionProps=false -p:ImportProjectExtensionTargets=false'
[ -n "${SELFCHECK_CMD_RESTORE+x}" ] && restore_cmd="$SELFCHECK_CMD_RESTORE"
if [ "$local_run" = 0 ] && [ -n "$restore_cmd" ]; then
  echo "selfcheck: restore (network: bridge)" >&2
  t0=$(date +%s%3N); out="$work/restore.out"
  run_in restore 900 "$restore_cmd" > "$out" 2>&1; rc=$?
  ms=$(( $(date +%s%3N) - t0 ))
  if [ $rc -eq 0 ]; then record restore pass "$ms" "$out"; echo "selfcheck: restore: pass (${ms} ms)" >&2
  else record restore fail "$ms" "$out"; restore_failed=1; echo "selfcheck: restore: fail (${ms} ms)" >&2; fi
fi

for s in "${stages[@]}"; do
  IFS='|' read -r name cmd secs <<<"$s"
  var="SELFCHECK_CMD_$(echo "$name" | tr 'a-z-' 'A-Z_')"; [ -n "${!var:-}" ] && cmd="${!var}"
  out="$work/$name.out"
  if [ "$restore_failed" = 1 ]; then
    echo "selfcheck: $name not run (restore failed)" >&2; : > "$out"; record "$name" not_run 0 "$out"; continue
  fi
  if [ "$build_failed" = 1 ] && [ "$name" != build ]; then
    echo "selfcheck: $name not run (build failed)" >&2; : > "$out"; record "$name" not_run 0 "$out"; continue
  fi
  echo "selfcheck: $name: $cmd" >&2
  t0=$(date +%s%3N)
  run_in stage "$secs" "$cmd" > "$out" 2>&1; rc=$?
  ms=$(( $(date +%s%3N) - t0 ))
  if [ $rc -eq 0 ]; then outcome=pass; else
    outcome=fail; any_fail=1; [ "$name" = build ] && build_failed=1
    [ $rc -eq 124 ] && echo "selfcheck: $name timed out after ${secs}s" >> "$out"
  fi
  echo "selfcheck: $name: $outcome (${ms} ms)" >&2
  record "$name" "$outcome" "$ms" "$out"
done

# --stage runs are for iteration and never count as a pass (the record, and the gate in a later slice, say so).
python3 - "$records" "$sha" "$any_fail" "${only:-}" "$restore_failed" "$local_run" "$image_id" <<'PY'
import json, sys, datetime
records, sha, any_fail, only, restore_failed, local_run, image_id = sys.argv[1:8]
stages = [json.loads(l) for l in open(records)]
print(json.dumps({
    "schema": "selfcheck/1", "commit": sha,
    "outcome": "could_not_run" if restore_failed == "1" else "fail" if any_fail == "1" else "pass",
    "env": "local" if local_run == "1" else "container", "image": image_id or None, "partial": bool(only),
    "stages": stages,
    "at": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}, indent=2))
PY
[ "$restore_failed" = 1 ] && exit 2
[ "$any_fail" = 0 ] && exit 0; exit 1
