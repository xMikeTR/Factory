#!/usr/bin/env bash
# selfcheck `docs` stage: cheap checks for the staleness the code review found. Runs in the tree under test.
#   1. No line in README.md, ARCHITECTURE.md or AGENTS.md is longer than 2,000 characters.
#   2. The test counts README.md states ("Core P/F", "Integration P/F") match what this run's tests-core and
#      tests-integration reported. Compared only for a stage that passed (--core-out / --int-out name its output).
#   3. Only one name for the improvement system appears in the Markdown files. docs.lock says which one:
#      {"improvement_system": {"name": "System 4", "aliases": ["S4", "System 3"]}}; an alias found as a whole word fails.
# Prints one JSON object (outcome, detail, warnings). Exit 0 pass, 1 fail.
set -u
core_out=""; int_out=""
while [ $# -gt 0 ]; do
  case "$1" in --core-out) core_out="${2:-}"; shift ;; --int-out) int_out="${2:-}"; shift ;; *) echo "docs: unknown argument: $1" >&2; exit 2 ;; esac
  shift
done
exec python3 - "$core_out" "$int_out" <<'PY'
import json, os, re, subprocess, sys
core_out, int_out = sys.argv[1:3]
fails, warns = [], []

for name in ("README.md", "ARCHITECTURE.md", "AGENTS.md"):
    if not os.path.exists(name): continue
    for i, line in enumerate(open(name, encoding="utf-8", errors="replace").read().split("\n"), 1):
        if len(line) > 2000: fails.append(f"{name}:{i} is {len(line)} characters long (limit 2000)")

def counts(path, pattern):
    if not path or not os.path.exists(path): return None
    m = re.findall(pattern, open(path, encoding="utf-8", errors="replace").read())
    return m[-1] if m else None
readme = open("README.md", encoding="utf-8", errors="replace").read() if os.path.exists("README.md") else ""
for label, path, pattern in (("Core", core_out, r"(\d+) passed, (\d+) failed"), ("Integration", int_out, r"Passed!\s+- Failed:\s+(\d+), Passed:\s+(\d+)")):
    stated = re.search(label + r" (\d+)/(\d+)", readme)
    got = counts(path, pattern)
    if not stated: continue
    if got is None: warns.append(f"README test count for {label} not compared (that stage did not pass in this run)"); continue
    passed, failed = (int(got[0]), int(got[1])) if label == "Core" else (int(got[1]), int(got[0]))
    if (int(stated.group(1)), int(stated.group(2))) != (passed, failed):
        fails.append(f"README.md says {label} {stated.group(1)}/{stated.group(2)}, the run reported {passed}/{failed}")

if os.path.exists("docs.lock"):
    cfg = json.load(open("docs.lock")).get("improvement_system", {})
    files = subprocess.run(["git", "ls-files", "*.md"], capture_output=True, text=True).stdout.split()
    for alias in cfg.get("aliases", []):
        rx = re.compile(r"(?<![\w])" + re.escape(alias) + r"(?![\w])")
        hits = [f for f in files if os.path.exists(f) and rx.search(open(f, encoding="utf-8", errors="replace").read())]
        if hits: fails.append(f"'{alias}' is an old name for the improvement system (docs.lock says '{cfg.get('name')}'): {', '.join(hits)}")
else:
    warns.append("no docs.lock: the improvement system name is not checked")

print(json.dumps({"outcome": "fail" if fails else "pass", "detail": "\n".join(fails), "warnings": warns}))
sys.exit(1 if fails else 0)
PY
