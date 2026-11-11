#!/usr/bin/env bash
# AC-10: a line of 2,001 characters in ARCHITECTURE.md fails docs. Also: stated test counts, old names, and a
# sabotage filter that matches nothing.
. "$(dirname "$0")/lib.sh"
d=$(mktemp -d); trap 'rm -rf "$d"' EXIT
fixture_repo "$d"; cd "$d"; mkdir -p scripts/selfcheck; cp "$SCRIPT_DIR"/selfcheck/*.sh scripts/selfcheck/
printf '# Arch\n%s\n' "$(head -c 2000 /dev/zero | tr '\0' x)" > ARCHITECTURE.md
printf '# Readme\nTests: Core 3/0, Integration 5/0.\nThe improvement system is System 4.\n' > README.md
echo '{"improvement_system":{"name":"System 4","aliases":["S4","System 3"]}}' > docs.lock
git add -A; git commit -qm docs
docs() { bash scripts/selfcheck/docs.sh "$@" > docs.json 2>/dev/null; }
docs || fail "2000 characters is allowed, and the control should pass"
printf '%s\n' "$(head -c 2001 /dev/zero | tr '\0' x)" >> ARCHITECTURE.md; git commit -qam long
docs; [ $? -eq 1 ] && grep -q "ARCHITECTURE.md:3 is 2001" docs.json || fail "a 2001-character line did not fail docs: $(cat docs.json)"
git reset -q --hard HEAD~1

echo "3 passed, 0 failed" > core.out; printf 'Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5\n' > int.out
docs --core-out core.out --int-out int.out || fail "matching counts should pass: $(cat docs.json)"
echo "4 passed, 0 failed" > core.out
docs --core-out core.out --int-out int.out; [ $? -eq 1 ] && grep -q "says Core 3/0, the run reported 4/0" docs.json || fail "a count mismatch did not fail"
docs --int-out int.out || fail "a stage that did not pass is not compared"

echo "We call it S4 here." >> README.md; git commit -qam alias
docs; [ $? -eq 1 ] && grep -q "'S4' is an old name" docs.json || fail "an old name did not fail"
git reset -q --hard HEAD~1

# sabotage: a filter that matches nothing fails the stage (dotnet itself would exit 0)
mkdir -p bin; printf '#!/usr/bin/env bash\necho "No test matches the given testcase filter"\nexit 0\n' > bin/dotnet; chmod +x bin/dotnet
PATH="$d/bin:$PATH" bash scripts/selfcheck/sabotage.sh > /dev/null 2>&1 && fail "sabotage passed with no matching test"
printf '#!/usr/bin/env bash\necho "Passed!  - Failed: 0, Passed: 21, Skipped: 0, Total: 21"\nexit 0\n' > bin/dotnet
PATH="$d/bin:$PATH" bash scripts/selfcheck/sabotage.sh > /dev/null 2>&1 || fail "sabotage should pass when tests match"
echo "ok AC-10"
