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
| `Core/Interop.fs` ⚠ high-centrality | expose ContextPack to C# | additive only |
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
the selection back from it (frozen). A capped turn ledgers `tool_call` outcome `capped`.

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
