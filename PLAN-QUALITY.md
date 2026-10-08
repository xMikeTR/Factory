# Plan — Quality strands (context cut, reviewer, quality gates, mutation, npm/TS, QA)

> Source: /grill-me session (2026-10-08). Baseline map: `ARCHITECTURE.md`. Earlier plan: `PLAN.md`.
> Scope file: `.claude/current-plan-scope.json` (ephemeral, rewritten per strand).

## Shared design (from the grill)

**Goal:** close the factory's quality gaps before a live repo: lean agent turns (context cut),
independent judgement (reviewer + QA on a different model family) and deterministic quality gates
(complexity, CRAP, mutation, fitness; ratcheted), with every gap found becoming a permanent
regression test.

**Out of scope:** Python quality checks (reported unsupported), F# mutation, dev as a test target,
automatic follow-up runs, the user's post-deploy E2E against dev, TDAD graph impact analysis.

**Constraints:** CPU contention causes timeouts (nothing heavy in the inner loop; heavy commands
serialised; a timeout is never evidence). No second provider locally (same-family only by override,
dry-run only). Containers hold no secrets and never get the Docker socket; the network opens only
through gates. Designc contracts reused (`escape`, `asbuilt_edge`, `fitness`).

**Decisions**
1. Context cut: exploration is the cause. Context pack = deterministic defaults + task-planner
   nominations (validated, never blocking); selection frozen per task, contents refreshed each turn,
   fixed budget. Tool-call cap 15 (warn at 12; at the cap verify runs as if done; `check_quality`
   exempt). Tool results older than the last 5 are stubbed.
2. Independence is an invariant: reviewer and QA come from a configured non-implementer pool,
   checked at start-up; Laya picks only the tier. Same family needs `FACTORY_REVIEWER_SAME_FAMILY=allow`,
   is stamped `independence: same-family`, and start-up refuses it unless merges are dry-run.
3. Reviewer: per task, after green, blocking on test-gaming/intent, bound decisions, security and
   equivalent-mutant confirmation; design quality advisory. Schema-enforced findings citing
   `file:line` in the diff; malformed -> retry once -> park, never pass. Weak tests -> TestAuthor
   (new red run), weak implementation -> Implementer. Read-only tools now, context pack later.
   Max 3 rounds then park with options (accept / reject / guidance); re-review = open findings +
   changed lines only. Review turns don't count as turns; send-back fix turns do.
4. Quality gates ratchet against the team baseline (touched code no worse, new code meets
   thresholds); pure F# over a neutral metrics format, adapters per language (C#, TS/React, F#;
   F# without mutation). Thresholds and modes (block/report) in protected `.factory/policy.yaml`.
   `check_quality` inner-loop tool: static only, reuses coverage from the last verify. Fitness:
   dependency rules from the Designc model's relationships + hand-written rules; baseline violations
   are debt, alerted via `asbuilt_edge` (Designc DC100). CPU: global semaphore on heavy commands,
   one isolated re-run of flaky-looking failures, timeouts never evidence (one re-run then park).
5. Mutation: per-task gate on the diff (`--since`), 80% on new lines, ratchet on touched lines,
   survivors -> TestAuthor; `RedEvidence.AgainstMutant`; equivalent mutants declared by the
   TestAuthor, confirmed by the reviewer.
6. npm: `npm ci --ignore-scripts` from the lockfile only, `PackageJsonChange.review` allow-list,
   registry pinned from the image, Playwright browsers baked in and version-matched; protected
   `npm.rebuild` allow-list runs offline after restore; skipped install scripts reported.
7. QA: per run after integration, requirement-level black-box, reviewer pool. Writes into the
   repo's Playwright project (human-owned scaffold). Environment from protected `qa-env.yaml`
   (harness-started sibling containers, isolated network, digests pinned, `fidelity` gaps declared).
   `new` tests fail on baseline and pass on integrated; `characterisation` pass on both (failing on
   baseline = escape). Every requirement needs a `new` test. Failure: nothing merges; escape +
   needs-decision to Designc; a human starts the follow-up run from the kept branch with QA tests
   protected. QA budget 8 turns; can't prove -> park.

**Strand order** (each its own plan section and handoff): 1 context cut · 2 reviewer + provider
seam · 3 quality gates C#/F# + CPU semaphore + timeout rule · 4 mutation + AgainstMutant ·
5 npm gate + TS quality · 6 QA.

---

## Strand 1 — Context cut

### Goal
Cut the tokens and calls an acting turn spends exploring, by giving the TestAuthor and Implementer
the code they need in the brief and bounding what a turn can accumulate.

**Success:** a TestAuthor/Implementer brief carries a `## Context` section (defaults + planner
nominations + the task's own written files, current contents, within budget); a turn stops at 15
tool calls and goes to verify; requests after the 5th tool result carry stubs instead of old
results; the selection survives resume (from the ledger).

### Module impact
| Module | Change | Blast radius |
|---|---|---|
| `Core/ContextPack.fs` (new, after `Domain.fs`) | pure selection + budget fitting | Interop, TeamRunner |
| `Core/Interop.fs` | not touched: C# calls `ContextPack` directly | none |
| `Harness/FactoryAgents.cs` | `TurnBudget` (cap/warn/exempt) in the governed middleware; `ToolResultTrimmer` in the chat pipeline | every agent; `Create` gains an optional parameter |
| `Harness/ScopedWorkspace.cs` | `submit_approach` tool for the task-planner | task-planner only |
| `S2/Teams/Briefs.cs` | `Task` gains the context pack | TeamRunner, BriefsTests |
| `S2/Teams/TeamRunner.cs` | build/freeze/refresh pack, pass TurnBudget, cap -> verify, recover selection on resume | TaskLoop only |
| `prompts/task-planner.md`, `test-author.md`, `implementer.md` | submit_approach; context section; cap | prompts |
| `policies/s2/task-planner.yaml` | allow `submit_approach` | policy contract test |

### Interface changes (flagged)
```fsharp
// Factory.Core/ContextPack.fs
type ContextSource = Default | Nominated of reason: string | TaskWritten
type ContextEntry = { Path: string; Source: ContextSource }
type ContextLimits = { MaxNominations: int; MaxChars: int }          // defaults 12, 60_000
type Nomination = { Path: string; Reason: string }
type Selection = { Entries: ContextEntry list; Rejected: (string * string) list }  // path, reason
type PackFile = { Path: string; Source: ContextSource; Text: string; Truncated: bool }

module ContextPack =
    /// Defaults: project files owning the task's write roots, one existing test file under
    /// testWrites (convention example), existing files matching writes/testWrites.
    val defaults : repoFiles: string list -> writes: PathPattern list -> testWrites: PathPattern list -> string list
    /// Validates nominations (normalised, exists, not .git/.factory, reason non-empty, no
    /// duplicates, at most MaxNominations). Rejections never fail the turn.
    val select : ContextLimits -> exists: (string -> bool) -> defaults: string list -> Nomination list -> Selection
    /// Adds the task's written files (frozen selection + this turn's TaskWritten), reads nothing.
    val withWritten : Selection -> written: string list -> ContextEntry list
    /// Fits contents into MaxChars: TaskWritten first, then defaults, then nominations in order;
    /// over budget, the last entries are head-truncated, then dropped.
    val fit : ContextLimits -> (ContextEntry * string) list -> PackFile list
```
```csharp
// Factory.Harness
public sealed class TurnBudget(int maxToolCalls = 15, int warnAt = 12, IReadOnlySet<string>? exempt = null)
{ public int Calls { get; } public bool Exhausted { get; } }        // one per turn
public AIAgent Create(AgentScope scope, ModelTier tier, string instructions, IList<AITool> tools, TurnBudget? budget = null);
public static IList<ChatMessage> ToolResultTrimmer.Trim(IList<ChatMessage> messages, int keepRecent); // pure
// ScopedWorkspace: TaskPlannerTools(Func<IReadOnlyList<Nomination>, IReadOnlyList<string>> validate)
//   = read_file, list_files, raise_needs_decision, submit_approach(note, context[{path, reason}])
// Briefs.Task(..., IReadOnlyList<PackFile> context)
```
Ledger: the `task_plan` turn payload gains `context: [{path, reason}]` and `rejected`; resume reads
the selection back from it (frozen). A capped call ledgers `tool_call` outcome `capped`; the act turn payload carries `capped`.

### Test strategy
- Core (F#, pure): `select` rejects missing/escaping/duplicate/over-limit nominations and keeps
  defaults; `fit` keeps TaskWritten whole before nominations, truncates last-first, never exceeds
  MaxChars; `defaults` picks the project file and one test file.
- Integration: `Trim` stubs all but the last 5 results and never touches the brief; a scripted
  agent making 16 calls gets the warning at 12, is stopped at 15, and the turn goes to verify;
  `submit_approach` with a bad path returns the rejection and the accepted selection reaches the
  next brief; the selection is recovered from the ledger after a restart; a brief shows the
  refreshed contents of a file the TestAuthor wrote.
- Most important failure case: a turn that hits the cap never loses work (verify runs on the worktree).

### Step sequence
1. `ContextPack.select`/`defaults` — Core tests.
2. `ContextPack.withWritten`/`fit` — Core tests (budget, order, truncation).
3. `ToolResultTrimmer` + wiring in `FactoryAgents.Create` — integration test on ScriptedChatClient requests.
4. `TurnBudget` (warn, cap, exempt, `capped` ledger row) — FactoryAgents test.
5. `submit_approach` tool + task-planner policy + prompt; TeamRunner stores note + selection and
   ledgers it — TeamRunner test (bad path rejected, good selection kept). ⚠ touches Harness + S2 + policy.
6. Brief `## Context` + per-turn refresh + resume recovery — TeamRunner/Briefs tests.
7. Cap -> verify in TaskLoop; prompts mention the context section and the cap — TeamRunner test.
8. ARCHITECTURE.md (module table, domain rules, Last updated); handoff.

### Risks
- **Biggest assumption:** that pre-loading context replaces exploration. If the agents keep
  exploring, the cap is what bites; the live run after strand 1 measures calls/tokens per turn.
- Head truncation is cruder than the grill's "signatures kept"; language-aware outlining is
  deferred (flagged deviation).
- The task-planner itself stays uncapped (it is where exploration is supposed to happen); its
  tool results are still trimmed.
- `FunctionInvocationContext.Terminate` semantics in MEAI 10.x: verify in step 4 that the turn ends cleanly.

---

## Strand 2 — Reviewer + provider seam

### Goal
An independent per-task review, blocking on what the grill agreed, on a model pool that is not the
implementer's. **Success:** a task cannot reach `Passed` without a review of its green diff; blocking
findings go back to the right role; the reviewer's provider is checked at start-up; the review's
independence is stamped in the ledger and the PR body.

### Slices (each committed green)
1. **Provider seam.** `ModelGateways` (implementer + reviewer `GatewayOptions`, family per side from
   `FACTORY_MODEL_FAMILY` / `FACTORY_REVIEWER_MODEL_FAMILY`, defaulting to `anthropic` for the Anthropic
   provider and unknown otherwise). Independent iff both known and different. Same/unknown family needs
   `FACTORY_REVIEWER_SAME_FAMILY=allow` and dry-run merges, else start-up refuses. `FactoryAgents` routes
   by role (`Roles.Reviewer`, `Roles.Qa` -> reviewer gateway). ⚠ `FactoryAgents` ctor gains a role-aware
   form (old form kept).
2. **Review rules (pure F#, `Core/Review.fs`).** `Finding` (category, file, line, message), categories
   `TestGaming | Intent | BoundDecision | Security | WeakTests | Design`; blocking = all but `Design`;
   `Review.validate` keeps only findings citing a file:line inside the diff (`DiffLines` parsed from a
   unified diff); `Review.route` sends `WeakTests` to the TestAuthor and the rest to the Implementer.
3. **Machine.** ⚠ `TaskState` gains `Reviewing`; events `ReviewCompleted`, `ReviewOverridden`; `Passed`
   needs review evidence (advisory findings + independence). Max 3 rounds then parked with options
   A accept with findings / B reject / C send back with guidance. Review turns don't count against
   `MaxTurns`; the send-back fix turns do. `Json.fs` encodes the new events. Core tests updated.
4. **`IReviewer` + `AgentReviewer` + TaskLoop.** Schema'd `submit_review` tool (invalid findings dropped,
   malformed -> retry once -> park, never pass), read-only tools, input = brief + diff; `ApprovingReviewer`
   default in the test harness; PR body and ledger carry `independence`. Policies + prompt `reviewer.md`.

**Done (strand 2).** Deviations from the slices above, all flagged in the commits:
- `TaskState` has `Reviewing` with events `ReviewCompleted | ReviewAccepted | ReviewGuided` (not `ReviewOverridden`);
  `NeedsDecisionRaised` is legal in `Reviewing` whenever unparked, so a review that cannot be had parks the
  task before the rounds run out; `ReviewGuided` resets the rounds only if they had run out.
- The re-review limit ("new findings only on lines the fix changed") is exact: each review ledgers a git tree
  snapshot (`GitWorkspace.SnapshotAsync`), and the next review diffs the worktree against it.
- Option ids A/B/C of a parked review: A accepts (open findings go on the PR via an `accepted` review row), B rejects, C guides.
- `Briefs.Task` is reused for the reviewer's brief, so it still says "You may write only these paths" with none listed.
- Not measured: whether the reviewer finds real problems on a live run (needs a paid run; ask first).

### Test strategy
- Core: `validate` drops findings outside the diff; `route`; the machine passes only through review;
  3rd blocking round parks; overrides.
- Integration: start-up independence matrix; a scripted reviewer's blocking finding sends the task back to
  the right role and a re-review passes; a malformed review parks (never passes); same-family stamp in the PR body.

### Risks
- Biggest assumption: one `Reviewing` state with a parked flag is enough for the options flow.
- Old ledgers (verify_completed straight to Passed) no longer replay to Passed: only matters for runs in
  flight across this change (none: live5 is gone).

## Strand 3 — Quality gates C#/F# + CPU semaphore + timeout rule

### Goal
Contention stops turning passing checks into timeouts, and a timeout is never evidence; then quality
(complexity, CRAP, fitness) is a ratcheted gate for C# and F#.

### Slices (each committed green)
- **3.1 CPU gate + timeout rule** ✅ `HeavyGate` (N slots, `FACTORY_HEAVY_SLOTS`, exclusive mode) and
  `GatedSandboxFactory` (every sandbox command; a timeout is re-run once alone, a second one is reported as 124).
  Core: `Verifying + NeedsDecisionRaised -> Blocked`. TaskLoop parks (source "environment") on any exit-124
  outcome in the red run, green verify and stash check; `RegressionAsync` returns a 124 outcome for a timed-out suite.
  Gap: a baseline timeout still turns P2P off silently.
- 3.2 ✅ Pure `Quality.fs`: neutral per-function metrics (file, name, line, cyclomatic, lines, coverage), CRAP, ratchet
  (new code meets thresholds; touched old code no worse; CRAP only where coverage data exists).
- 3.3 ✅ (C#) `CSharpMetrics` (Roslyn syntax, no compilation), `Cobertura.Join`, `QualityPolicyFile` (`quality:` in
  `.factory/policy.yaml`: mode off|report|block), `QualityGate` (diff vs integration branch), TaskLoop `QualityOutcomeAsync`
  (block: failing `quality` outcome joins green; report: ledger row stage `quality`). `FactorySettings.QualityPolicy` added.
  Not yet: F# adapter (needs a parser decision: FSharp.Compiler.Service is not in the offline cache), coverage collection
  (coverlet via the task's verify), report mode on the PR body.
- 3.4 ✅ `check_quality` (Implementer only, present when quality mode is not off; exempt from the cap; same ratchet as verify).
- 3.5 ✅ (csproj edges) `Handover.Relationships` (FLAGGED contract widening; optional in JSON, schema updated, read from the
  DSL `a -> b` between code-owning elements), `Fitness` (pure), `CsprojEdges`, `QualityGate.FitnessAsync`; violations join the
  quality outcome (block/report) and `check_quality`. Baseline element pairs are excluded (debt).
- **Deferred from strand 3** (recorded, not forgotten): F# adapter (FCS not in the offline cache; token heuristic vs FCS is a
  decision), coverage collection (coverlet in the task's verify; CRAP is skipped without data), report-mode findings on the PR
  body, `asbuilt_edge` debt alerts to Designc (format not in this repo), hand-written fitness rules, one isolated re-run of
  flaky-looking failures, a baseline suite timeout still turns P2P off silently.
- Open: ArchUnitNET vs NetArchTest.

### Interface changes (flagged)
- `TaskState`: `Verifying` now accepts `NeedsDecisionRaised` (-> `Blocked`). `FactoryServices.Sandboxes` is a gated factory.

## Strand 4 — Mutation + `RedEvidence.AgainstMutant`

### Goal
Tests are judged by whether they kill mutants of the new code, not only by passing. A task whose tests leave
too many mutants alive on the lines it changed goes back to the TestAuthor; tests that cannot fail before the
change (the behaviour already exists) can still be valid red evidence if they kill a mutant.

### Slices (each committed green)
- 4.1 ✅ Pure Core `Mutation.fs`: `MutantStatus` (Killed|Survived|Timeout|NoCoverage|CompileError|Ignored), `Mutant`
  (file, line, mutator, status, id), `score` (killed+timeout over detected-able; CompileError/Ignored excluded),
  `check thresholds baseline mutants changedRanges` (80% on new lines, ratchet on touched lines), equivalents declared
  by the TestAuthor and excluded only once the reviewer confirmed them.
- 4.2 ✅ `RedKind.AgainstMutant` (FLAGGED: widens a closed union; Json codec + exhaustive matches updated) and
  `RedEvidence.CreateAgainstMutant(outcomes, killed)`: all-green outcomes accepted only with >= 1 killed mutant.
- 4.3 ✅ `StrykerReport` parser (S2/Quality, fixture-tested) behind `IMutationRunner` (the real tool cannot be installed
  offline here; the image install is a later, separate step with `./docker/runner/build.sh`).
- 4.4 ✅ TaskLoop: mutation gate after green verify when `quality.mutation` is on (off|report|block); survivors go back
  to the TestAuthor as a brief; timeouts never evidence.

- Also landed: `Verifying + ReturnedToTestAuthor -> Ready(TestAuthor)` (Failed at the attempt limit); `VerifiedEvidence` exempts
  `AgainstMutant` red from the failing stash check; `TestsDoneAsync` tries a mutation run over the existing `Writes` files when a
  red run is all green; `IMutationRunner` seam on `FactoryServices` (null = no tool, gate skipped); `quality.mutation` and
  `quality.mutationMinScore` in the policy (default off, 0.8).
- **Deferred from strand 4**: the real `StrykerRunner` and its image install (Stryker.NET not installable offline here; add via
  `dotnet tool install --tool-path /opt/dotnet-tools` in docker/runner/Dockerfile, then `./docker/runner/build.sh`), the
  TestAuthor's equivalent-mutant declaration tool and the reviewer's confirmation (pure rule exists: `Mutation.confirmedEquivalents`),
  the per-file baseline score for the ratchet (the check takes it; the runner passes none yet, so the flat 80% applies), F#/TS mutation.

### Interface changes (flagged)
- `Verifying` accepts `ReturnedToTestAuthor`. `FactoryServices` ctor gains an optional `IMutationRunner`. `QualityPolicy` gains an optional `Mutation`.
- `RedKind` gains `AgainstMutant`; `RedEvidence` gains a second constructor. Policy gains `quality.mutation`.

### Risks
- Stryker.NET not installable offline: everything is behind the seam and fixture-tested; live behaviour unmeasured.

## Strand 5 — npm gate + TS quality

### Goal
A TypeScript target repo restores and verifies under the same rule as .NET: network only inside a gated restore window over
reviewed inputs, no install scripts, registry fixed by the image; and TS code meets the quality ratchet.

### Slices (each committed green)
- 5.1 ✅ Pure Core `PackageJsonChange.review` (package.json: registry-semver dependency specs only, no install-time lifecycle scripts,
  no bundled deps; package-lock.json: new/changed `resolved` URLs on the pinned registry; `.npmrc` cannot change), same shape as
  `ProjectFileChange.review`; `affects` extends the restore inputs; `Core.ReviewRestoreInput` dispatches.
- 5.2 ✅ `RestoreGate` plans npm restores: one `npm ci --ignore-scripts` per package.json that has a lockfile, registry pinned from
  the image's npmrc; a package.json without a lockfile is refused (no lockfile = no reproducible restore).
- 5.3 ✅ Protected `npm.rebuild` allow-list in `.factory/policy.yaml`: packages whose install script may run offline after restore;
  scripts skipped (lockfile `hasInstallScript`) and not allowed are reported. (policy + report line; the run happens in the image.)
- 5.4 ✅ TS quality adapter: parse `eslint --format json` (complexity, max-lines-per-function) into neutral `FunctionMetrics`;
  same Core ratchet. Fixture-tested; the eslint run belongs to the image.
- Not wired yet: running `RestoreGate.RebuildCommand` in the sandbox after the restore and putting `SkippedInstallScripts` on the
  PR body (the commands, policy and report are built and tested; they need node in the image); `EslintMetrics` is not called from
  `QualityGate` (the eslint run needs the image).
- Deferred: baking node, npm and Playwright browsers into docker/runner/Dockerfile (needs network at build; then `./docker/runner/build.sh`),
  ArchUnit-equivalent for TS (dependency-cruiser rules from the model's relationships), coverage for TS CRAP.

### Interface changes (flagged)
- `Core.ReviewRestoreInput` now also judges package.json, package-lock.json and .npmrc; `RestoreGate.PlanAsync` may add npm restores
  and refusals; policy gains an `npm.rebuild` list.

## Strand 6 — QA

### Goal
After the integration checks pass, black-box tests written at requirement level (by the reviewer pool, never the Implementer's)
judge the integrated result against the baseline. A failure merges nothing and raises an escape + needs-decision to Designc.

### Slices (each committed green)
- 6.1 ✅ Pure Core `Qa.fs`: `QaKind` (New | Characterisation), per-test baseline/integrated outcomes, `judge`:
  every requirement needs a `new` test; `new` must fail on baseline and pass on integrated (passing on baseline = vacuous,
  failing on integrated = not done); `characterisation` must pass on both (failing only on integrated = regression ESCAPE,
  failing on baseline = invalid test). Wording flag: the grill note said "failing on baseline = escape"; a characterisation
  test that fails on the baseline cannot describe existing behaviour, so it is an invalid test, and the escape is the regression.
- 6.2 ✅ `QaEnvFile` (protected `.factory/qa-env.yaml`): sibling services with digest-pinned images, `fidelity` gaps declared.
- 6.3 ✅ `IQaRunner` seam (null = QA skipped) + `QaGate` and the `IntegrationVerifyExecutor` hook: a failing judgement ends the run
  (`qa_failed`), writes `qa` ledger rows and a Designc needs-decision brief. Budget constant 8 turns for the author.
- Deferred: the QA author agent itself (reviewer pool, prompt, tool policy, writes into the repo's Playwright project, 8-turn
  budget), starting the sibling containers from `qa-env.yaml`, running the tests on baseline and integrated, the human follow-up run
  with QA tests protected, and the open question where requirements come from (the handover carries none yet).

### Interface changes (flagged)
- `FactoryServices` ctor gains an optional `IQaRunner`; `IntegrationVerifyExecutor` consults it after the checks pass.
