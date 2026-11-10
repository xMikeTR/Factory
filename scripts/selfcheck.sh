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
#   scripts/selfcheck.sh --stage <name>  one stage (build | tests-core | tests-integration | sabotage | contracts | docs)
#   scripts/selfcheck.sh --designc <dir> where designc is checked out (else FACTORY_DESIGNC_DIR, else ../designc)
#   scripts/selfcheck.sh --strict-contracts  a fixture behind designc HEAD fails instead of warning
#   scripts/selfcheck.sh --local         on the host, no container
#   scripts/selfcheck.sh --status [sha]  the last recorded result for a commit (default HEAD)
#   scripts/selfcheck.sh --install-hook  install the pre-push gate (main and release/* need a current container pass)
#   scripts/selfcheck.sh --checks-hash [sha]  the checks_hash of the checks as committed at sha (default: the files on disk)
#
# Each run is appended as a git note under refs/notes/selfcheck (compact JSON, one line per run).
# Environment: FACTORY_CONTAINER_ENGINE (podman by default), FACTORY_RUNNER_IMAGE (factory-s2-runner:latest).
# Exit codes: 0 pass, 1 a check failed, 2 could not run (no engine, no image, restore failed, bad arguments),
# 3 no record (--status only). --status exits 0 only for a full, container, passing record; a --stage or --local
# pass is printed but exits 1, because it never counts as a pass.
set -u

commit=HEAD; only=""; local_run=0; status=0; mode=""; hook_args=(); designc="${FACTORY_DESIGNC_DIR:-}"; strict_contracts=0
while [ $# -gt 0 ]; do
  case "$1" in
    --local)  local_run=1 ;;
    --commit) commit="${2:-}"; shift ;;
    --stage)  only="${2:-}"; shift ;;
    --designc) designc="${2:-}"; shift ;;
    --strict-contracts) strict_contracts=1 ;;
    --install-hook) mode=install ;;
    --checks-hash)  mode=hash; hash_given=0; if [ $# -gt 1 ] && [ "${2#--}" = "$2" ]; then commit="$2"; hash_given=1; shift; fi ;;
    --pre-push)     mode=prepush; shift; hook_args=("$@"); break ;;
    --status) status=1; if [ $# -gt 1 ] && [ "${2#--}" = "$2" ]; then commit="$2"; shift; fi ;;
    *) echo "selfcheck: unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

# checks_hash: sha256 over scripts/selfcheck.sh and scripts/selfcheck/**, hashed file by file so the paths count too.
hash_checks_in() { (cd "$1" && { sha256sum scripts/selfcheck.sh; [ -d scripts/selfcheck ] && find scripts/selfcheck -type f | LC_ALL=C sort | xargs -r sha256sum; } | sha256sum | cut -d' ' -f1); }
hash_checks_at() { # <repo> <sha>: the checks as committed
  local t; t=$(mktemp -d "${TMPDIR:-/tmp}/selfcheck-hash.XXXXXX") || return 1
  git -C "$1" ls-tree -r --name-only "$2" -- scripts/selfcheck.sh scripts/selfcheck | xargs -r git -C "$1" archive "$2" 2>/dev/null | tar -x -C "$t" 2>/dev/null
  hash_checks_in "$t"; rm -rf "$t"
}

if [ "$mode" = hash ]; then
  hrepo=$(git rev-parse --show-toplevel) || exit 2
  if [ "$hash_given" = 0 ]; then hash_checks_in "$(cd "$(dirname "$0")/.." && pwd)"; else hash_checks_at "$hrepo" "$(git -C "$hrepo" rev-parse "$commit^{commit}")"; fi
  exit 0
fi

if [ "$mode" = install ]; then
  irepo=$(git rev-parse --show-toplevel 2>/dev/null) || { echo "selfcheck: not inside a git repository" >&2; exit 2; }
  hook="$(git -C "$irepo" rev-parse --path-format=absolute --git-path hooks)/pre-push"
  if [ -e "$hook" ] && ! grep -q "Installed by scripts/selfcheck.sh --install-hook" "$hook"; then
    echo "selfcheck: $hook already exists and is not ours; not overwriting it" >&2; exit 2
  fi
  mkdir -p "$(dirname "$hook")"
  cat > "$hook" <<'HOOK'
#!/usr/bin/env bash
# Installed by scripts/selfcheck.sh --install-hook. Refuses a push to main or release/* without a current container pass.
root=$(git rev-parse --show-toplevel) || exit 1
[ -f "$root/scripts/selfcheck.sh" ] || { echo "selfcheck: scripts/selfcheck.sh is missing in $root; push refused" >&2; exit 1; }
exec bash "$root/scripts/selfcheck.sh" --pre-push "$@"
HOOK
  chmod +x "$hook"; echo "selfcheck: installed $hook"; exit 0
fi

if [ "$mode" = prepush ]; then
  prepo=$(git rev-parse --show-toplevel) || exit 1
  remote="${hook_args[0]:-}"; refused=0; here_hash=""
  while read -r lref lsha rref rsha; do
    [ -n "${lsha:-}" ] || continue
    case "$lsha" in *[!0]*) ;; *) continue ;; esac               # a delete pushes no commit
    case "$rref" in refs/heads/main|refs/heads/release/*) ;; *) continue ;; esac
    # Only the tip is gated: a passing run of the tip covers every commit under it. (The spec says "each commit"; see the slice notes.)
    want=$(hash_checks_at "$prepo" "$lsha")
    note=$(git -C "$prepo" notes --ref=selfcheck show "$lsha" 2>/dev/null)
    reason=$(python3 - "$want" "$note" <<'PY'
import json, sys
want, note = sys.argv[1], sys.argv[2]
recs = []
for l in note.splitlines():
    try: recs.append(json.loads(l))
    except Exception: pass
if not recs: print("no selfcheck record"); sys.exit()
full = [r for r in recs if r.get("env") == "container" and not r.get("partial")]
if not full: print("only --local or --stage records (they never count)"); sys.exit()
cur = [r for r in full if r.get("checks_hash") == want]
if not cur: print("the record was made with different checks (stale checks_hash)"); sys.exit()
print("" if cur[-1].get("outcome") == "pass" else "the last record is " + str(cur[-1].get("outcome")))
PY
)
    if [ -n "$reason" ]; then
      echo "selfcheck: push of ${lsha:0:12} to $rref refused: $reason" >&2
      echo "selfcheck: run:  scripts/selfcheck.sh --commit ${lsha:0:12}   (then push again; git push --no-verify skips this gate)" >&2
      refused=1
    fi
  done
  [ "$refused" = 0 ] || exit 1
  # Send the notes with the push when they differ from the remote's (a failure here never blocks the push).
  if [ -n "$remote" ] && lsn=$(git -C "$prepo" rev-parse --verify --quiet refs/notes/selfcheck); then
    rsn=$(git -C "$prepo" ls-remote "$remote" refs/notes/selfcheck 2>/dev/null | cut -f1)
    [ "$lsn" = "$rsn" ] || git -C "$prepo" push --no-verify -q "$remote" refs/notes/selfcheck:refs/notes/selfcheck \
      || echo "selfcheck: could not push refs/notes/selfcheck (a second machine may need: git fetch $remote refs/notes/selfcheck:refs/notes/selfcheck)" >&2
  fi
  exit 0
fi

if [ "$status" = 1 ]; then
  srepo=$(git rev-parse --show-toplevel 2>/dev/null) || { echo "selfcheck: not inside a git repository" >&2; exit 2; }
  ssha=$(git -C "$srepo" rev-parse --verify --quiet "$commit^{commit}") || { echo "selfcheck: no such commit: $commit" >&2; exit 2; }
  note=$(git -C "$srepo" notes --ref=selfcheck show "$ssha" 2>/dev/null | grep -v '^[[:space:]]*$' | tail -n 1)
  [ -n "$note" ] || { echo "selfcheck: no record for ${ssha:0:12}" >&2; exit 3; }
  python3 - "$note" <<'PY'
import json, sys
r = json.loads(sys.argv[1]); print(json.dumps(r, indent=2))
sys.exit(0 if r.get("outcome") == "pass" and not r.get("partial") and r.get("env") == "container" else 1)
PY
  exit $?
fi
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
  "build|dotnet build Factory.sln -warnaserror{NR}|900"
  "tests-core|dotnet run --project tests/Factory.Core.Tests{NR}|900"
  "tests-integration|dotnet test tests/Factory.Integration.Tests{NR}|1800"
  "sabotage|bash scripts/selfcheck/sabotage.sh{NR}|900"
  "contracts|bash scripts/selfcheck/contracts.sh|120"
  "docs|bash scripts/selfcheck/docs.sh|60"
)
# contracts runs on the host, not in the container: it only reads files and runs `git show` in the designc checkout.
[ -n "$designc" ] || designc="$(dirname "$(git rev-parse --show-toplevel 2>/dev/null)")/designc"
designc=$(cd "$designc" 2>/dev/null && pwd || echo "$designc") # absolute: the stage runs from the worktree
# In the container the restore already ran, and the stages have no network: a second implicit restore would only
# try (and fail) to reach nuget.org, e.g. for the vulnerability audit (NU1900, an error under -warnaserror).
NR=""; [ "$local_run" = 0 ] && NR=" --no-restore"
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
build_failed=0; any_fail=0; restore_failed=0; cnr=0; core_ok=0; int_ok=0
warnings_file="$work/warnings.jsonl"; : > "$warnings_file"; designc_commit=""; designc_head=""

# Same limits, user and mounts as the factory's own sandbox (DockerSandbox.LimitArgs / BaseEnv).
user="$(id -u):$(id -g)"
cargs=(--init --cap-drop ALL --security-opt no-new-privileges --user "$user" --tmpfs /tmp:exec,mode=1777
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
  run_in restore 900 "$restore_cmd" > "$out" 2>&1 & wait $!; rc=$?
  ms=$(( $(date +%s%3N) - t0 ))
  if [ $rc -eq 0 ]; then record restore pass "$ms" "$out"; echo "selfcheck: restore: pass (${ms} ms)" >&2
  else record restore fail "$ms" "$out"; restore_failed=1; echo "selfcheck: restore: fail (${ms} ms)" >&2; fi
fi

for s in "${stages[@]}"; do
  IFS='|' read -r name cmd secs <<<"${s//\{NR\}/$NR}"
  var="SELFCHECK_CMD_$(echo "$name" | tr 'a-z-' 'A-Z_')"; [ -n "${!var:-}" ] && cmd="${!var}"
  out="$work/$name.out"
  if [ "$restore_failed" = 1 ]; then
    echo "selfcheck: $name not run (restore failed)" >&2; : > "$out"; record "$name" not_run 0 "$out"; continue
  fi
  if [ "$build_failed" = 1 ] && { [ "${name#tests-}" != "$name" ] || [ "$name" = sabotage ]; }; then
    echo "selfcheck: $name not run (build failed)" >&2; : > "$out"; record "$name" not_run 0 "$out"; continue
  fi
  echo "selfcheck: $name: $cmd" >&2
  t0=$(date +%s%3N)
  if { [ "$name" = contracts ] || [ "$name" = docs ]; } && [ -z "${!var:-}" ]; then
    # Host stage. The script prints one JSON object; its detail becomes the stage tail, its warnings go in the record.
    if [ "$name" = contracts ]; then cargs_c=(--designc "$designc"); [ "$strict_contracts" = 1 ] && cargs_c+=(--strict)
    else cargs_c=(); [ "$core_ok" = 1 ] && cargs_c+=(--core-out "$work/tests-core.out"); [ "$int_ok" = 1 ] && cargs_c+=(--int-out "$work/tests-integration.out"); fi
    if [ -f "$tree/scripts/selfcheck/$name.sh" ]; then
      (cd "$tree" && timeout "$secs" bash "scripts/selfcheck/$name.sh" "${cargs_c[@]}") > "$out.json" 2> "$out.err"; rc=$?
    else echo "scripts/selfcheck/$name.sh is not in this commit" > "$out.err"; : > "$out.json"; rc=2; fi
    python3 - "$out.json" "$out.err" "$out" "$warnings_file" > "$out.meta" <<'PY'
import json, sys
j, e, out, wf = sys.argv[1:5]
try: d = json.load(open(j))
except Exception: d = {"detail": open(e).read(), "warnings": []}
open(out, "w").write(d.get("detail") or open(e).read())
with open(wf, "a") as f:
    for w in d.get("warnings", []): f.write(json.dumps(w) + "\n")
print(d.get("designc_commit") or "", d.get("designc_head") or "")
PY
    if [ "$name" = contracts ]; then read -r designc_commit designc_head < "$out.meta"; [ -n "${designc_commit:-}" ] || designc_commit=""; fi
    ms=$(( $(date +%s%3N) - t0 ))
    if [ $rc -eq 0 ]; then outcome=pass; elif [ $rc -eq 2 ]; then outcome=could_not_run; cnr=1; else outcome=fail; any_fail=1; fi
    echo "selfcheck: $name: $outcome (${ms} ms)" >&2
    record "$name" "$outcome" "$ms" "$out"; continue
  fi
  # Background + wait: bash runs a trap only between commands, so a TERM mid-stage would otherwise wait for the stage to end.
  run_in stage "$secs" "$cmd" > "$out" 2>&1 & wait $!; rc=$?
  ms=$(( $(date +%s%3N) - t0 ))
  if [ $rc -eq 0 ]; then outcome=pass; else
    outcome=fail; any_fail=1; [ "$name" = build ] && build_failed=1
    [ $rc -eq 124 ] && echo "selfcheck: $name timed out after ${secs}s" >> "$out"
  fi
  echo "selfcheck: $name: $outcome (${ms} ms)" >&2
  [ "$outcome" = pass ] && { [ "$name" = tests-core ] && core_ok=1; [ "$name" = tests-integration ] && int_ok=1; }
  record "$name" "$outcome" "$ms" "$out"
done

# checks_hash ties a result to the version of the checks that actually ran: this script and its scripts/selfcheck/** on disk.
# The pre-push gate compares it with the same files as committed at the pushed tip.
checks_hash=$(hash_checks_in "$(cd "$(dirname "$0")/.." && pwd)")
by=$(git -C "$repo" config user.name 2>/dev/null || true)

# --stage runs are for iteration and never count as a pass (the record, and the gate in a later slice, say so).
python3 - "$records" "$sha" "$any_fail" "${only:-}" "$restore_failed" "$local_run" "$image_id" "$checks_hash" "$by" "$work/record.json" "$cnr" "$designc_commit" "$designc_head" "$warnings_file" <<'PY'
import json, sys, datetime
records, sha, any_fail, only, restore_failed, local_run, image_id, checks_hash, by, outpath, cnr, dc, dh, wf = sys.argv[1:15]
warnings = [json.loads(l) for l in open(wf)]
stages = [json.loads(l) for l in open(records)]
rec = ({
    "schema": "selfcheck/1", "commit": sha,
    "outcome": "could_not_run" if restore_failed == "1" else "fail" if any_fail == "1" else "could_not_run" if cnr == "1" else "pass",
    "env": "local" if local_run == "1" else "container", "image": image_id or None, "partial": bool(only),
    "checks_hash": checks_hash, "designc_commit": dc or None, "designc_head": dh or None,
    "stages": stages, "warnings": warnings,
    "at": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "by": by or None,
})
print(json.dumps(rec, indent=2))
open(outpath, "w").write(json.dumps(rec))
PY
# Append to the note (a later run on the same commit adds a line; --status reads the last). A failure to write is a warning, not a verdict.
git -C "$repo" notes --ref=selfcheck append -F "$work/record.json" "$sha" >/dev/null 2>&1 \
  || echo "selfcheck: could not write the git note (is user.name/user.email set?); the record above was not stored" >&2
[ "$restore_failed" = 1 ] && exit 2
[ "$any_fail" = 1 ] && exit 1
[ "$cnr" = 1 ] && exit 2
exit 0
