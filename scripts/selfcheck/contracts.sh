#!/usr/bin/env bash
# selfcheck `contracts` stage: are the designc fixtures copied into this repo still honest?
#   contracts.sh --designc <dir> [--strict]   check (run from the tree under test); JSON on stdout
#   contracts.sh --designc <dir> --update     re-copy every fixture from designc HEAD and rewrite the lock (run in the repo)
# Reads contracts.lock.json. Uses `git show <commit>:<path>` in the designc checkout, so its working copy never matters.
# Exit: 0 pass, 1 fail, 2 could not run (no designc checkout, no lock, pinned commit unknown).
# Pinned: each fixture must be byte-identical to its source at designc_commit (a difference = someone edited a copy).
# Ahead:  each source at designc HEAD vs the pinned commit; a difference is a warning (a fail with --strict).
set -u
designc=""; strict=0; update=0
while [ $# -gt 0 ]; do
  case "$1" in
    --designc) designc="${2:-}"; shift ;;
    --strict)  strict=1 ;;
    --update)  update=1 ;;
    *) echo "contracts: unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done
exec python3 - "$designc" "$strict" "$update" <<'PY'
import json, subprocess, sys, os
designc, strict, update = sys.argv[1], sys.argv[2] == "1", sys.argv[3] == "1"

def done(outcome, code, detail="", warnings=(), pinned=None, head=None):
    print(json.dumps({"outcome": outcome, "detail": detail, "warnings": list(warnings), "designc_commit": pinned, "designc_head": head}))
    sys.exit(code)

def git(*a, raw=False):
    r = subprocess.run(["git", "-C", designc, *a], capture_output=True)
    if r.returncode != 0: return None
    return r.stdout if raw else r.stdout.decode().strip()

if not designc or git("rev-parse", "--git-dir") is None:
    done("could_not_run", 2, "no designc checkout (use --designc <dir>, FACTORY_DESIGNC_DIR, or ../designc)")
head = git("rev-parse", "HEAD")
try: lock = json.load(open("contracts.lock.json"))
except Exception as e: done("could_not_run", 2, f"cannot read contracts.lock.json: {e}")
files = lock["files"]

if update:
    for f in files:
        data = git("show", f"{head}:{f['source']}", raw=True)
        if data is None: done("could_not_run", 2, f"{f['source']} does not exist at designc HEAD {head[:12]}")
        open(f["fixture"], "wb").write(data)
    lock["designc_commit"] = head
    json.dump(lock, open("contracts.lock.json", "w"), indent=2); open("contracts.lock.json", "a").write("\n")
    done("pass", 0, f"updated {len(files)} fixtures to designc {head[:12]}", pinned=head, head=head)

pinned = lock["designc_commit"]
if git("cat-file", "-e", f"{pinned}^{{commit}}") is None:
    done("could_not_run", 2, f"pinned designc commit {pinned[:12]} is not in the designc checkout", pinned=pinned, head=head)
fails, warns = [], []
for f in files:
    want = git("show", f"{pinned}:{f['source']}", raw=True)
    have = open(f["fixture"], "rb").read() if os.path.exists(f["fixture"]) else None
    if want is None: fails.append(f"{f['source']} does not exist at pinned designc {pinned[:12]}")
    elif have != want: fails.append(f"{f['fixture']} differs from {f['source']} at designc {pinned[:12]} (edited by hand?)")
    log = git("log", "--oneline", f"{pinned}..{head}", "--", f["source"])
    if log:
        n = len(log.splitlines())
        msg = f"{os.path.basename(f['fixture'])} is behind designc HEAD ({n} commit{'s' if n != 1 else ''}): " + "; ".join(l for l in log.splitlines()[:3])
        (fails if strict else warns).append(msg)
done("fail" if fails else "pass", 1 if fails else 0, "\n".join(fails), warns, pinned, head)
PY
