# RESOURCES: what to write yourself, and how

Nine areas (12 `TODO(you)` stubs), chosen because each one is either a rule the factory stands on or a seam
you will have to defend in design reviews. Everything else (git plumbing, the process runner,
the ledger, JSON codecs, prompts, schemas) is already written: read it, don't retype it.

Find them all with:

```bash
grep -rn "TODO(you)" src tests
```

## Work order

| # | TODO | File | Spec | Why you, not me |
|---|---|---|---|---|
| 1 | `mayOverlap`, `isWithin` | `Factory.Core/Paths.fs` | tests `paths:` | Soundness: which way to be wrong, and why |
| 2 | `TaskState.transition` | `Factory.Core/TaskState.fs` | tests `state:` | The lifecycle *is* the harness; budgets live here |
| 3 | `findCycle`, `validate` | `Factory.Core/PlanValidator.fs` | tests `validator:` | Your "nothing done without a check" in code |
| 4 | `Dispatcher.dispatch` | `Factory.Core/Dispatcher.fs` | tests `dispatcher:` | Parallelism without write collisions |
| 5 | `Merge.decide` | `Factory.Core/Merge.fs` | tests `merge:` | Tiered merge autonomy, your earlier proposal |
| 6 | `TaskLoop.RunAsync`, `NextTurnAsync` | `Factory.S2/Teams/TeamRunner.cs` | the resume drill | The turn loop: configurator, signals, scope |
| 7 | `VerifyRunner.RunTaskAsync` | `Factory.S2/Verify/VerifyRunner.cs` | T-001 in a real repo | Cleanup semantics make self-seeded checks honest |
| 8 | `S2Workflow.Build` | `Factory.S2/Workflow.cs` | the app runs end to end | MAF graphs, conditions, RequestPort round trips |
| 9 | `GovernedToolCallAsync` | `Factory.Harness/FactoryAgents.cs` | a denied tool call in the ledger | The governance seam where mlang/ACS plugs in |

Do 1 to 5 first. They are pure F#, need no network, and turn the test runner green:

```bash
dotnet run --project tests/Factory.Core.Tests     # starts at 5 passed, 25 failed
```

The existing tests are your acceptance criteria. If you change a rule, change its test first.
Items 6 to 9 need MAF and are best done during the Phase 0 spike.

---

## 1. Paths: `mayOverlap` and `isWithin`

**The idea.** Both functions answer questions that can't be answered exactly for globs in general,
so you decide which way to be wrong, and the right direction is opposite for the two:

- `mayOverlap` feeds team grouping. Saying "yes" when the answer is no only costs parallelism.
  Saying "no" when it is yes lets two agents write the same file at once. So over-approximate.
- `isWithin` feeds the validator. Saying "no" when it is yes rejects a valid plan, and the planner
  retries. Saying "yes" when it is no lets a task write outside its element. So under-approximate.

This is abstract interpretation in miniature: when you can't be exact, be sound in the
direction that protects the invariant.

**Gotchas:**
- `**` matches zero segments, so `src/**` matches `src` itself.
- `src/Quotes*/**` is *not* within `src/Quotes.Data/**`, because `src/Quotes2/x` escapes it. The
  test "prefix glob escapes" pins this down.

**Read:**
- Russ Cox, *Glob Matching Can Be Simple And Fast Too*: https://research.swtch.com/glob. It
  explains the `*` backtracking that the provided `matchSeg` uses.
- Scott Wlaschin, *Designing with types: making illegal states unrepresentable*:
  https://fsharpforfunandprofit.com/posts/designing-with-types-making-illegal-states-unrepresentable/.
  This is why `PathPattern` has a private representation and `create` returns a `Result`.

**Stretch.** Property tests with FsCheck (https://fscheck.github.io/FsCheck/):
- for random `a`, `b` and path `p`, if `matches a p && matches b p` then `mayOverlap a b`;
- if `isWithin i o && matches i p` then `matches o p`.

These two properties are the real specification; the unit tests are examples of it.

## 2. `TaskState.transition`

**The idea.** A pure function `state -> event -> Result<state, error>`. The table is in the
doc comment. Three details matter:

- Budgets are checked on the way *into* `Running` and `Verifying`, not by the agent or the prompt.
- `Passed` can only be built from a `PassedEvidence`, and `PassedEvidence.Create` refuses an
  empty list or any non-zero exit code. You can't write a transition that passes without
  evidence, because the type won't allow it.
- `replay` is already written as a fold over your `transition`. Resume from the ledger works as
  soon as `transition` does.

**Gotchas:**
- Keep `FS0025` (incomplete pattern match) as an error; it is set in the fsproj.
- The terminal case goes first: `| (Passed _ | Failed _), _ -> illegal ()`.
- `Abandon` comes next and matches every non-terminal state with a single or-pattern.

**Read:**
- Wlaschin, *Designing with types: Making state explicit*:
  https://fsharpforfunandprofit.com/posts/designing-with-types-representing-states/
- Wlaschin, *Railway oriented programming*: https://fsharpforfunandprofit.com/rop/. `replay` is
  `Result.bind` in a fold.
- F# discriminated unions reference:
  https://learn.microsoft.com/en-us/dotnet/fsharp/language-reference/discriminated-unions
- Book: *Domain Modeling Made Functional* (Wlaschin, Pragmatic Bookshelf), chapters on
  workflows as state machines.

**Stretch.** Model the same state machine in P, with a spec monitor asserting that `Passed` is
only reached after a `VerifyCompleted` whose outcomes all exited 0. This is the plan's point that
"the P model of the pipeline and the code share one shape". P docs: https://p-org.github.io/P/.

## 3. `findCycle` and `validate`

**The idea.** One rule per `PlanError` case. Collect every error rather than stopping at the
first, because the whole list goes back to the planner agent in one retry. `PlanError.describe`
is already written; read it, since it is also the prompt text the planner sees.

**Gotchas:**
- `ProtectedWrite` is checked with `mayOverlap` (any overlap is too much). `WriteOutsideElement`
  is checked with `isWithin` against the code areas of the task's *own* elements. Report only one
  of the two per write path.
- `CoverWithoutBinding`: a task may only claim to prove a decision if it touches at least one
  element that the decision binds.
- `findCycle`: use the DFS with three colours (unvisited, on the stack, finished). The cycle is
  the stack slice from the node you hit back to the current node. A task that depends on itself
  counts as a cycle.
- `ValidatedPlan`'s constructor is `internal`. Keep it that way, so that `validate` stays the
  only way to create one.

**Read:**
- Depth-first search and cycle detection (the "colouring" section):
  https://en.wikipedia.org/wiki/Depth-first_search
- Wlaschin, *Validation with F#* (applicative style, collecting errors):
  https://fsharpforfunandprofit.com/posts/elevated-world-3/. The provided code uses a mutable
  `ResizeArray` for clarity. Rewriting it as an applicative `Validation` is a good exercise if
  you want it purer.

## 4. `Dispatcher.dispatch`

**The idea.** Four steps, listed in the doc comment. The helpers (`tasksOverlap`, `scc`,
`topoBy`) are provided, because you don't learn much from re-implementing Tarjan. The judgement
is in how you combine them:

- **Union-find over overlapping writes.** Tasks that might touch the same file are made one team,
  so they run one after another in the same worktree.
- **Cycles between groups.** If group A depends on B and B depends on A, the two can never run in
  either order, so they are merged into one team. The test "cross-team cycle collapses" is
  exactly the A1 → B1 → A2 case.
- **Waves.** These are the levels of Kahn's algorithm on the team DAG.

**Gotchas:**
- Determinism. Sort the tasks first, and give teams and waves stable ids and orders. The same
  plan must always dispatch the same way, or resume breaks.
- With path compression in `find`, always compare roots, never raw parent entries.

**Read:**
- Disjoint-set / union-find: https://en.wikipedia.org/wiki/Disjoint-set_data_structure
- Tarjan's SCC: https://en.wikipedia.org/wiki/Tarjan%27s_strongly_connected_components_algorithm
- Topological sorting (Kahn): https://en.wikipedia.org/wiki/Topological_sorting

**Stretch.** FsCheck property: for any valid plan, no two teams in the dispatch have overlapping
writes, and every task appears in exactly one team.

## 5. `Merge.decide`

**The idea.** A list of `(tier, reason)` rules, take the maximum tier, and keep every reason for
the PR body. It is small, but it is the policy that sets how autonomous the factory is, so it
should be yours.

**Gotchas:** "touched elements" means the elements of the teams, looked up in the handover by id.
The rule "a changed file outside every declared write" is the backstop that catches scope leaks
the earlier guards missed.

**Next step.** Move `MergePolicy` out of `defaultPolicy` and into an mlang contract, so the
autonomy thresholds are governed and versioned like the other policies.

## 6. The turn loop: `TaskLoop.RunAsync` and `NextTurnAsync`

**The idea.** This is the harness. The helpers are already there:
- `ApplyAsync`, which writes the ledger row and then transitions;
- `EnforceScopeAsync`, which reverts out-of-scope changes and logs them;
- `CommitAsync`;
- `LastApproachNoteAsync`;
- `Workspace(signals)`;
- `Briefs.Task(...)`.

**Invariants to hold yourself to:**
- The only thing that assigns `state` is `ApplyAsync`. If anything else does, replay and reality
  diverge.
- Agents signal through tools (`declare_done`, `raise_needs_decision`), never through prose you
  parse.
- Each turn gets a fresh brief rebuilt from the plan, the handover and the ledger, never a
  growing chat history.
- A turn that changed nothing counts towards a stall, and the configurator escalates on stalls.

**Gotchas:**
- Handle `Running` on resume: the process died mid-turn. Close the turn with `TurnEnded` and
  move on.
- Use a needs-decision answer at most once per call.
- On a replayed `Passed`, only commit leftover changes if all of them are inside this task's
  writes.

**Read:**
- MAF agents, and running agents inside workflows:
  https://learn.microsoft.com/en-us/agent-framework/workflows/agents-in-workflows
- Luke Alvoeiro's Factory talk on orchestrator/worker/validator and fresh-context handoffs. This
  loop is his validator pattern with a deterministic validator.
- Dex Horthy, *12-factor agents*: https://github.com/humanlayer/12-factor-agents. See especially
  "own your control flow" and "compact errors into context"; `Briefs.Failures` is the second.

## 7. `VerifyRunner.RunTaskAsync`

**The idea.** Run the verify entries exactly as the plan froze them. Cleanup *always* runs (use
`finally`-style ordering with `CancellationToken.None`), and a failed cleanup counts as a failure,
because leftover seeded data makes the next run's assertions meaningless.
`FACTORY_VERIFY_NS` is what lets self-seeded checks namespace their data.

**Gotcha.** F#'s `None` is `null` in .NET, so `a.Cleanup?.Value` is the idiom.

**Read:**
- Your July verify-blocks spec (invariant, self-seeded and manual kinds; implementer can't edit
  verify). This TODO implements it.
- `Process.Kill(entireProcessTree: true)`, which `ProcessRunner` uses:
  https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill

## 8. `S2Workflow.Build`

**The idea.** Wire the graph from the edge list in the comment. What you learn here is how MAF
routes messages:
- conditional edges;
- one message type per edge;
- `RequestPort`s, whose responses come back to the executor that sent the request;
- `WithOutputFrom`.

**Gotchas:**
- Check the conditional `AddEdge` overload against your installed version.
- A `RequestPort` needs edges in both directions: requester → port and port → requester.
- MAF runs in supersteps with a barrier between them. That is why the team loop is not a MAF
  sub-workflow.

**Read:**
- Executors: https://learn.microsoft.com/en-us/agent-framework/workflows/executors
- Edges and conditions: https://learn.microsoft.com/en-us/agent-framework/workflows/edges
- Human-in-the-loop and RequestPort:
  https://learn.microsoft.com/en-us/agent-framework/workflows/human-in-the-loop
- Sub-workflows, for comparison with the choice made here:
  https://learn.microsoft.com/en-us/agent-framework/workflows/advanced/sub-workflows
- The superstep/BSP discussion: https://github.com/microsoft/agent-framework/issues/7555

## 9. `GovernedToolCallAsync`

**The idea.** Function-calling middleware runs before every tool call of every agent. It
evaluates the policy, writes a ledger row either way, and on denial returns a message the model
can act on *without* calling `next`. Today the policy is `RoleAllowListPolicy`; the Phase 0 exit
criterion is to replace it with the ACS runtime loading mlang-compiled manifests.

**Read:**
- MAF agent middleware (run, function-calling and chat-client levels):
  https://learn.microsoft.com/en-us/agent-framework/agents/middleware
- MAF + AGT, *Governance at the speed of agents*:
  https://devblogs.microsoft.com/agent-framework/governance-at-the-speed-of-agents-microsoft-agent-framework-and-agent-governance-toolkit-better-together/
- AGT FAQ (framework integration types): https://microsoft.github.io/agent-governance-toolkit/FAQ/
- ACS policy engine: https://github.com/microsoft/agent-governance-toolkit/blob/main/policy-engine/README.md

**Stretch.** Wrap the ACS evaluator behind `IToolPolicy`, so the harness has no dependency on
AGT types and the allow-list becomes the test double.

---

## Definition of done

- `dotnet run --project tests/Factory.Core.Tests` shows 30 passed.
- `factory-s2 validate` and `factory-s2 dispatch` work on the example and on one plan from your
  own impl-plan skill.
- T-001 runs against a real repo in dry-run PR mode, and the ledger shows:
  - task events that replay to `Passed`;
  - tokens on every turn;
  - a verify row for every attempt.
- The resume drill (kill mid-turn, rerun with the same `--run-id`) redoes no finished work.
- A deliberately denied tool call shows up as a `tool_call` row with outcome `denied`.

Keep the original `factory-s2.zip` as the reference implementation, but try each TODO before you
look. Where your version differs, write down why; those notes are good Substack material.
