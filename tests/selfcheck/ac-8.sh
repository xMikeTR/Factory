#!/usr/bin/env bash
# AC-8: the hook refuses a push to main with no record, a failing record, a --local record and a stale-checks record;
# it allows one with a current passing container record. Other branches are not gated; the notes ride along.
. "$(dirname "$0")/lib.sh"
d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
fixture_repo "$d/w"; git init -q --bare "$d/remote.git"; cd "$d/w"
git branch -m main; git remote add origin "$d/remote.git"
bash scripts/selfcheck.sh --install-hook > /dev/null || fail "install failed"
bash scripts/selfcheck.sh --install-hook > /dev/null || fail "re-install should be idempotent"
sha=$(git rev-parse HEAD); hash=$(bash scripts/selfcheck.sh --checks-hash "$sha")
[ "${#hash}" = 64 ] && [ "$hash" = "$(bash scripts/selfcheck.sh --checks-hash)" ] || fail "checks-hash on disk and at the commit disagree"
note() { # env partial outcome hash
  python3 -c "import json,sys; print(json.dumps({'schema':'selfcheck/1','commit':'$sha','env':'$1','partial':$2,'outcome':'$3','checks_hash':'$4','stages':[]}))" > rec.json
  git notes --ref=selfcheck add -f -F rec.json "$sha"
}
refused() { git push -q origin main > /dev/null 2> push.err && fail "push should have been refused: $1"; grep -q "$2" push.err || fail "$1: wrong reason: $(cat push.err)"; }

refused "no record" "no selfcheck record"
note container False fail "$hash";  refused "failing record" "last record is fail"
note local False pass "$hash";      refused "local record" "never count"
note container True pass "$hash";   refused "partial record" "never count"
note container False pass "stale";  refused "stale checks" "stale checks_hash"
git push -q origin HEAD:refs/heads/feature > /dev/null 2>&1 || fail "a push to a feature branch must not be gated"
note container False pass "$hash"
git push -q origin main > /dev/null 2> push.err || fail "current passing record should allow the push: $(cat push.err)"
[ "$(git -C "$d/remote.git" rev-parse refs/notes/selfcheck)" = "$(git rev-parse refs/notes/selfcheck)" ] || fail "the notes did not travel with the push"
# a later --local record does not shadow the container pass
echo more > more.txt; git add -A; git commit -qm next; sha=$(git rev-parse HEAD); hash=$(bash scripts/selfcheck.sh --checks-hash "$sha")
note container False pass "$hash"
python3 -c "import json; print(json.dumps({'env':'local','partial':False,'outcome':'fail','checks_hash':'$hash'}))" > loc.json
git notes --ref=selfcheck append -F loc.json "$sha"
git push -q origin main > /dev/null 2> push.err || fail "a later local record must not shadow the pass: $(cat push.err)"
# changing the checks after the run makes the record stale
echo "# x" >> scripts/selfcheck.sh; git commit -qam "change the checks"; sha=$(git rev-parse HEAD)
note container False pass "$hash"; refused "checks changed after the run" "stale checks_hash"
echo "ok AC-8"
