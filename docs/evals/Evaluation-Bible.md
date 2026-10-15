# Evaluation Bible: Design-to-Code Factory

Oct 6, 2026 · @Mike

## How to use this bible

This is the one method for evaluating the design-to-code factory: one loop, one vocabulary, one task format. Every tool, article and framework you read later is an implementation detail that slots into it.

**The one rule:** an eval exists because a failure was observed, never because one was imagined. The current suite breaks this rule, so it is treated as a draft until error analysis confirms or replaces it.

Three principles carry over from the factory itself:

- **Design is the source of truth.** Eval tasks come from frozen handover packages and bound decisions, so they regenerate when the design changes.
- **Probabilistic proposes, deterministic decides.** Prefer a code grader; reach for an LLM judge only for what code cannot check.
- **Nothing is done without a check.** Every check emits a ledger event, so System 4 can learn from it.

To extend it: add a section only when a real eval run taught you something. Log the change in the changelog at the end, with the date and the failure that prompted it.

## Vocabulary

Use these words and only these. Most of the confusion between sources is the same idea under different names; the right column maps them onto factory parts.

| Term | Meaning | In the factory |
| --- | --- | --- |
| Task | One test case with defined inputs and success criteria | A frozen handover slice plus its bound decisions, e.g. pricing-v3 with D-0042 |
| Trial | One attempt at a task; run several because agents are non-deterministic | One S2 run of that task in a fresh worktree |
| Grader | Logic that scores one aspect of a trial; holds one or more checks | A decision's `checks:` entry, a verify block, an LLM judge |
| Transcript (trace) | Full record of a trial: messages, tool calls, reasoning | The Langfuse trace, linked from the ledger by `trace_id` |
| Outcome | Final state of the environment after the trial | The worktree diff, test results, merged PR, ledger events |
| Agent harness | The scaffold that makes a model an agent | MAF agents built by `AddFactoryAgent(role)` |
| Eval harness | The infrastructure that runs tasks, records trials, grades and aggregates | The runner you build around S2 (section on harness design) |
| Suite | A set of tasks sharing a goal | `factory/suites/s2-regression`, `factory/suites/grill-me` |
| Capability eval | Starts with a low pass rate; a hill to climb | New design patterns S2 cannot yet build |
| Regression eval | Should pass \~100%; a drop means something broke | Graduated capability tasks plus every past escape |
| Gate | A threshold that blocks progress | `ReadyGate`, `PlanValidator`, merge tiers |
| Gradient | A score tracked over time that guides change but blocks nothing | CRAP score, mutation score, tokens per passing task |
| Failure mode | A named, recurring way the system fails, found by reading traces | A row in the failure taxonomy (playbook section) |
| Escape | A problem caught later than the stage that should have caught it | The S4 escape classification |

The last two are the bridge between Hamel and your factory: error analysis finds failure modes; System 4 classifies escapes. Same activity, two scales.

## The one loop

Every source you have read describes the same four-step loop; they differ only in names and tooling. When you feel lost, find which step you are on.

&#91;embedded content: the eval loop · 4 steps\]

The look step is highlighted because it is the one most often skipped. Hamel's Level 1 and Level 2 tests are step 3; his A/B tests and Anthropic's pass^k are step 4; Anthropic's eight-step roadmap is steps 3 and 4 in detail.

## Where evals live in the factory

Evals wrap System 2 from the outside: the eval harness reads tasks from the registry, drives S2 trials, and writes results to the ledger that System 4 reads. Nothing new is invented; it reuses the registry lifecycle, the ledger and the escape table from the factory plan.

&#91;embedded content: evals in the design-to-code factory · 7 parts\]

| System | What its suite asks | First graders |
| --- | --- | --- |
| System 1 | Does grill-me ask the question that resolves each open finding? Does ReadyGate reject incomplete packages? | Question-quality judge; F# tests per gate clause |
| System 2 | Does the merged code satisfy the package and every bound decision, within cost? | Build, tests, decision checks, scope, then judges |
| System 4 | No suite of its own. Each proposal carries a task that fails on the old version and passes on the new. | The proposal's own check |

Start with System 2. It has the most deterministic graders and the existing 11 tasks to learn from.

## Grader catalogue

Pick the cheapest grader that can see the failure: code first, an LLM judge only for what code cannot check, a human only to calibrate the judge. Grade what was produced, not the path taken; agents find valid routes you did not anticipate.

| Grader | Checks | Type | Gate or gradient | Factory source |
| --- | --- | --- | --- | --- |
| Build | Solution compiles with warnings-as-errors | Code | Gate | `VerifyRunner` |
| Fail-to-pass tests | The task's new tests now pass | Code | Gate | Task verify blocks |
| Pass-to-pass tests | Existing tests still pass | Code | Gate | `VerifyRunner` |
| Decision checks | Every bound decision's `checks:` passes on merged code | Code | Gate | Registry `decisions/*.yaml` |
| Behavioural model | P property holds; PObserve finds no violation in test logs | Code | Gate | `p check`, PObserve |
| Scope | Diff touches only the task's `writes` paths | Code | Gate | ACS policy log plus a diff check |
| Static analysis | Analyzers, nullable, type check clean | Code | Gate | `dotnet build`, `tsc --strict` |
| Code quality metrics | CRAP score, mutation score, property tests | Code | Gradient, then gate | Thresholds encoded in mlang |
| Regeneration | Deleted component rebuilt from the package passes every check | Code | Gate (scheduled) | Regeneration drill |
| Cost and effort | Tokens, turns and reruns per passing task | Code | Gradient | Ledger metrics |
| Quality rubric | Root cause fixed, no test gaming, codebase fit, no over-engineering | LLM judge | Gradient | Rubric playbook |
| Plan quality | Planner's tasks are well-scoped with verify blocks | LLM judge plus `PlanValidator` | Gate (validator), gradient (judge) | S2 planner |
| Question quality | Grill-me asks the question that resolves the open finding | LLM judge | Gradient | S1 grill-me |
| Human review | Labels a sample to calibrate judges | Human | Neither; calibration only | Weekly S4 review |

Two rules for promotion. A gradient becomes a gate only after its threshold has been stable for several weeks, the gate-vs-gradient framing you already use. An LLM judge never becomes a gate on its own; pair it with a code check or a human.

## Task and suite format

Every task is one YAML file in the registry, reviewed by PR like a decision. It follows the same lifecycle (minted, checked, bound), so System 4 can propose tasks and only a human can bind them.

```
registry/
  factory/
    tasks/S2-0001.yaml        # one task per file
    suites/s2-regression.yaml # list of task ids + run settings
    rubrics/test-gaming.md    # one judge prompt per dimension
    labels/test-gaming.yaml   # your human labels' calibration rounds, one file per dimension
    taxonomy.yaml             # failure modes from error analysis
```

A task file:

```yaml
id: S2-0001
status: bound                  # minted | checked | bound | retired
kind: regression               # capability | regression
system: S2
source:                        # why this task exists (the one rule)
  failure_mode: FM-03          # from taxonomy.yaml
  trace_id: lf-7c1e...         # the Langfuse trace that showed it
input:
  handover: handovers/pricing-v3@v3.1.0
  task_filter: [quote-writes]
environment:
  repo: pricing-engine
  commit: 4f2a9e1              # frozen start state
  fresh_worktree: true
graders:                       # all must pass unless weighted below
  - {type: build}
  - {type: tests, fail_to_pass: [QuoteConcurrencyTests], pass_to_pass: all}
  - {type: decision_checks, decisions: [D-0042]}
  - {type: scope, allowed: from_task_writes}
  - {type: judge, rubric: rubrics/test-gaming.md}
trials: 5
scoring: binary                # binary | weighted | hybrid
tracked: [tokens, turns, reruns, cost, wall_clock]
reference_solution: refs/S2-0001.patch   # proves the task is solvable
```

A suite file only lists task ids, the trial count and the pass metric, so one task can sit in several suites.

Three checks before a task is bound: two engineers would give the same verdict on any trial; the reference solution passes every grader; and everything a grader checks is stated in the input. A 0% pass rate over many trials usually means a broken task, not a weak agent.

## Playbook: error analysis

This is the step every source agrees on and the one your current suite skipped. Budget two evenings for the first pass; it produces the taxonomy everything else hangs off.

1. **Collect 50–100 traces.** Mix real S2 runs with the 11 existing tasks rerun k times. Include passes as well as failures; quiet failures hide in passing runs.
2. **Open coding.** Read each trace and write one plain sentence about the first thing that went wrong, e.g. "executor edited QuoteService outside its writes paths". Note only the first error; later ones are usually downstream of it. Record pass or fail for the trial as a whole.
3. **Axial coding.** Group the sentences into 5–10 failure modes. Give each an id (FM-01), a one-line definition and one example trace. An LLM can propose clusters; you decide them.
4. **Count.** Tally traces per failure mode. The top three by frequency, weighted by cost to you, are what you work on.
5. **Triage each mode.** Ask which fix is cheapest:
   - A spec gap (handover was ambiguous): fix System 1, add a grill-me question or gate clause. No eval needed yet.
   - A harness or prompt bug with an obvious fix: fix it. Add a regression task so it stays fixed.
   - A recurring judgment failure: write a grader for it, code if possible, judge if not.
6. **Stop when saturated.** If 20 consecutive traces add no new mode, stop. Repeat the whole pass after any big change: new model, new prompt, new harness version.

Keep the result in `factory/taxonomy.yaml`:

```yaml
- id: FM-03
  name: Scope breach
  definition: Executor writes outside the task's writes paths.
  example_trace: lf-7c1e...
  count: 14 / 80
  fix: grader              # spec | fix | grader
  graders: [scope]
```

The classification in step 5 is the same as System 4's escape table: a spec gap is an S1 escape, a runtime failure is an S2 escape. Run error analysis by hand inside the weekly S4 review until the patterns are stable; that is Phase A of System 4.

## Playbook: LLM judges

A judge is a probabilistic component, so it proposes and something deterministic decides: its score is trusted only after it agrees with your labels. Write one judge per failure mode, never one judge for "quality".

Rules:

- **Binary verdicts.** PASS or FAIL per dimension. Scales from 1 to 5 drift and are hard to calibrate.
- **One dimension per call.** Isolated judges are easier to calibrate and to debug.
- **An exit.** Allow UNKNOWN when the evidence is missing, so the judge does not guess.
- **Reasoning before verdict.** The JSON puts `reasoning` first.
- **Strongest model you can afford.** The judge can be slower and pricier than the agent; it runs offline.
- **Pin everything.** Model id, prompt version and rubric hash go into every result.

Starting dimensions for S2, to be kept only if error analysis finds them: scope, root cause, test gaming, codebase fit, over-engineering, readability.

Template, stored as `factory/rubrics/<dimension>.md`:

```
You review a code change for ONE property: {dimension}.

Task given to the agent: {task}
Bound decisions: {decisions}
Diff: {diff}
Tests before and after: {tests}

FAIL if: {fail_conditions}
PASS otherwise.
UNKNOWN if the inputs do not contain enough to decide.

Examples:
{2 labelled examples: one PASS, one FAIL, with reasons}

Respond as JSON only:
{"reasoning": "...", "verdict": "PASS|FAIL|UNKNOWN"}
```

### Calibration

1. Label 40 trials yourself for the dimension, PASS or FAIL with one line of critique. Aim for about half failures; if real failures are rare, add known-bad patches.
2. Split them: 15 to develop the prompt, 25 held out.
3. Run the judge on the held-out set and compute precision and recall on FAIL, not raw agreement.
4. Iterate the prompt on the development set only, using your critiques, until both are at least 0.8 on held-out. Then freeze its version.
5. Re-check monthly with 20 fresh labels, and after any model change.

Record each round in `factory/labels/<dimension>.yaml` as `rounds: [{date, judge_version, precision, recall, n}]`. That file is the evidence that the judge can be trusted; Improve flags a dimension whose latest round is below 80% or older than 30 days.

## Eval harness design

The eval harness is a thin MAF workflow, or a plain CLI to start, that runs System 2 as the thing under test. It reuses the factory's own parts: the worktree manager, `VerifyRunner`, the ledger and the Langfuse exporter. Build the CLI first; promote it to a workflow only when runs need to pause or resume.

Per trial:

1. **Isolate.** Fresh worktree at the task's pinned commit, empty caches, no git history from earlier trials. Shared state either leaks answers or causes correlated failures.
2. **Run.** Invoke S2 exactly as production does, through the gateway with the same ACS policies. An eval agent that differs from production measures the wrong thing.
3. **Capture.** Keep the Langfuse trace id, the final diff, test output and ledger events.
4. **Grade.** Code graders first; skip the judges if a gate already failed, to save tokens.
5. **Record.** One result row per grader per trial, tagged with the versions below.
6. **Tear down.** Delete the worktree.

Every result carries: `harness_version` (git sha of the factory), `model`, `prompt_version`, `configurator` choice, `task_id`, `trial`, and `suite_run_id`. Without them you cannot tell whether a score moved because of your change.

### Reading scores

For a per-trial success rate p over k trials:

```latex
\text{pass@}k = 1 - (1-p)^k \qquad \text{pass}^k = p^k
```

Use pass@1 to compare versions and pass^k (k = 3 to 5) for regression suites: the factory merges code, so one success in five is not good enough. At p = 0.75, pass^3 is about 0.42.

Early on, 20–50 tasks are enough because changes have large effects. Before believing any score move, open the failing transcripts and confirm the agent failed, not the grader or the environment.

## Observability

Evals tell you whether a version is better, offline; observability tells you what every real run did. They share one trace format, so production traces become the input to the next error analysis, and the loop above runs on live data instead of imagined cases.

### Three signals

| Signal | Holds | Store | Answers |
| --- | --- | --- | --- |
| Traces | Every span of one run: agent turns, tool calls, gates | Langfuse, via MAF OpenTelemetry | What happened in this run? |
| Events | Gates, findings, needs-decision, escapes, grader results | Ledger (JSONL in git, `ledger/factory.jsonl`) | What changed state, and why? |
| Metrics | Aggregates of the two above | Ledger queries, later New Relic | Is the factory getting better or worse? |

The join key is `trace_id`: every ledger event carries it, so any number on a dashboard can be opened as the traces behind it.

### What to measure

Start with the bold rows; add others only when a decision depends on them.

| Metric | Definition | Use | Source |
| --- | --- | --- | --- |
| **First-pass rate** | Tasks passing `VerifyRunner` on the first attempt ÷ tasks started | S2 quality trend | Ledger `task`, `gate` |
| **Tokens per passing task** | All tokens in a handover ÷ tasks that passed | Headline efficiency | Langfuse usage, ledger |
| **Escape rate per stage** | Problems caught later than the stage that should have caught them, by stage | System 4's headline | Ledger `escape` |
| **Stall rate** | Tasks escalated because they stalled ÷ tasks | Where agents get stuck; entry to behavioural evals | Ledger `task_event` Abandon whose reason starts "not making progress" (a stall fails the task; it is not a `needs_decision`) |
| **Spend per user** | £ per user per month against the £750 budget | Budget and alerts | APIM v2 |
| Reruns per task | Verify failures before pass | Main cost lever | Ledger `task.reruns` |
| Turns per task | Agent turns until done or escalated | Prompt and routing changes | Langfuse spans |
| Needs-decision per handover | Escalations back to System 1 per package | Spec quality signal for S1 | Ledger |
| Policy denials per run | ACS denials, by rule | Scope pressure; prompt drift | ACS log, ledger |
| Tool error rate | Failed calls ÷ calls, per tool | Broken tools vs bad usage | Langfuse spans |
| Cache hit ratio | Cached input tokens ÷ input tokens | Cache control in your harness | Langfuse usage |
| Configurator accuracy | Jev or Laya choice vs task outcome | Calibrating routing | Ledger |
| Human wait time | Time runs spend paused at a `RequestPort` | Where you are the bottleneck | MAF checkpoints, ledger |
| Regeneration drill | Scheduled drill passes or fails | "Code is disposable" holds | Ledger |

### How to instrument

1. **Traces.** Keep MAF's OpenTelemetry export to Langfuse. Use the OpenTelemetry GenAI semantic-convention names where they exist (model, token usage, tool name) so any backend can read them.
2. **Factory attributes.** Add the same tags to every span, set once at the run's root: `factory.run_id`, `factory.system`, `factory.handover`, `factory.task_id`, `factory.decisions`, `factory.harness_version`, `factory.prompt_version`, `factory.model_tier`, `factory.configurator`.
3. **Events.** `LedgerMiddleware` and `LedgerExecutor` write a ledger row for every state change, with `trace_id`. Gates and graders write their verdict there first, then as a Langfuse score.
4. **Cost.** APIM is the source of truth for money; Langfuse tokens are for per-task analysis. Reconcile the two monthly; a gap means an untraced caller.
5. **Masking.** In a regulated setting prompts can carry client data. Mask known sensitive fields before export and keep raw payloads in the ledger only where access is controlled.

### Views and alerts

Three views, each built for one decision:

- **Run view** (Langfuse trace): why did this run fail? Opened from a ledger row.
- **Factory health** (daily, ledger queries): first-pass rate, tokens per passing task, stall rate, spend per user, each against last week.
- **Improvement review** (weekly, System 4): escape rate per stage and the top failure modes, which feed the error-analysis playbook.

Alert on few things, all actionable: a user above 80% of budget; stall rate double its 4-week average; a spike in ACS denials for one rule; the regression suite's pass^3 dropping after a merge to the factory.

### Online evals

Run the code graders on every production run, since they are cheap. Run LLM judges on a sample of about 10% of runs. Send runs where graders disagree, or a judge returns UNKNOWN, to an annotation queue; they are the first traces you read in the next error-analysis pass.

One rule from System 4 applies here too: track escapes, never raw finding counts. A metric with no decision behind it is a panel nobody opens; delete it.

### Anatomy of one run's trace

&#91;embedded content: span tree of one System 2 run · 10 spans\]

Set the factory tags once on the root span and propagate them to children, so filtering by `task_id` or `harness_version` returns whole runs. Every metric in the table above is a query over these spans and the ledger rows that share their `trace_id`.

## Langfuse as the backend

Langfuse stores and shows; the registry decides. Tasks, rubrics, labels and the taxonomy live in git, so you can swap Langfuse for New Relic or GCP later without losing the method.

| Need | Where it lives | Langfuse feature used |
| --- | --- | --- |
| Traces of every trial | Langfuse, via MAF OpenTelemetry | Traces and observations |
| Grader results | Ledger (truth) and Langfuse (view) | Scores attached to the trial's trace |
| Open coding notes | Langfuse while annotating, exported to git after | Annotation queues and score configs |
| Task definitions | Registry YAML | Optional mirror as a dataset, never edited there |
| Version comparison | Ledger queries | Optional: dataset runs side by side |
| Judge calibration labels | Registry `factory/labels/` | None |

Three conventions make the rest easy:

- Tag each trial's trace with `task_id`, `suite_run_id` and `harness_version`, so a filter gives you one run.
- Name scores `grader.<name>` with values 1 (pass), 0 (fail) or null (unknown).
- Write every grader result to the ledger first and Langfuse second. The ledger is what System 4 reads.

## Worked example: Quote lost-update (D-0042)

The same slice the factory plan uses, taken once through the whole loop. The trace contents below are illustrative; yours will differ, which is the point of looking.

1. **Look.** Run the pricing-v3 handover through S2, 5 trials, plus the 11 existing tasks rerun 3 times each. That gives about 38 traces.
2. **Name.** Open coding produces notes such as: "added rowversion but retried forever on conflict"; "edited `PricingController` outside writes"; "tcOptimistic passes but the new test only asserts no exception". Axial coding groups them into, say, FM-01 unbounded retry, FM-03 scope breach, FM-05 weak test.
3. **Triage.**
   - FM-01 is a spec gap: D-0042 never said what to do on conflict. Add a grill-me question ("retry or surface to the caller?") and a gate clause. That is an S1 escape.
   - FM-03 has an obvious fix: tighten the executor's ACS policy. Add task S2-0001 with the scope grader so it stays fixed.
   - FM-05 recurs and code cannot see it. Write the test-gaming judge, then calibrate it on 40 labelled trials.
4. **Test.** Bind S2-0001 to S2-0003 in `s2-regression`, each with build, tests, the D-0042 check, scope, and the judge where relevant.
5. **Measure.** Change one thing, e.g. the executor prompt. Rerun the suite with the same trials and compare pass^3 and tokens per passing task against the previous `harness_version`.
6. **Feed System 4.** Each failure mode becomes a ledger `escape` event classified by stage. The next weekly review starts from those counts.

What proves the loop works: a new task fails on the old prompt and passes on the new one. That is the same proof the factory plan asks of every System 4 proposal.

## Starter plan: first two weeks

The goal for two weeks is one regression suite you trust, not a framework. Each item is useful on its own if time runs out.

**Week 1: look and name**

- [ ] Tag S2 traces with `task_id`, `trial`, `harness_version`
- [ ] Run the 11 existing tasks 3 times each and the pricing-v3 slice 5 times
- [ ] Open-code every trace in a Langfuse annotation queue, one sentence each
- [ ] Cluster into 5–10 failure modes and write `factory/taxonomy.yaml` with counts
- [ ] Triage each mode as spec, fix or grader

**Week 2: test and measure**

- [ ] Create `registry/factory/` with the task schema and 3–5 regression tasks from the top modes
- [ ] Write the reference solution for each task and confirm every grader passes on it
- [ ] Build the eval CLI: isolate, run, capture, grade, record, tear down
- [ ] Write results to the ledger (`designc record`, see `docs/factory-evals-contract.md`) and as Langfuse scores
- [ ] Make one change and compare pass^3 and tokens per passing task against the baseline

**Done when:** you can say, with a trace to show, "this change made S2 better at X and no worse at Y". Judges come after this, for whichever mode code could not catch.

## Changelog, open questions and references

| Date | Change | Prompted by |
| --- | --- | --- |
| 2026-10-06 | First version | Existing suite written from imagination |

### Open questions

- [ ] Should System 1 get its own suite now (grill-me question quality), or only after S2's suite is stable?
- [ ] Where do eval runs execute long term: locally, CI, or GCP alongside the data scientists?
- [ ] Does Langfuse stay the trace store, or does New Relic take it over under Agentic Ops?
- [ ] Which code quality metrics (CRAP, mutation, property tests) get thresholds first?

### References

Read these when a section above stops being enough, not before.

- [Your AI Product Needs Evals](https://hamel.dev/blog/posts/evals/), Hamel Husain: the three levels and the case for looking at data
- [Demystifying evals for AI agents](https://www.anthropic.com/engineering/demystifying-evals-for-ai-agents), Anthropic: vocabulary, coding-agent graders, pass@k and pass^k, the eight-step roadmap
- [Automating Error Analysis](https://hamel.dev/notes/llm/ai-product-engineering/evals-error-analysis.html), Hamel Husain and Shreya Shankar: taxonomy-building with active learning
- [How to build agent evals from traces](https://arize.com/?p=31604), Arize: a second worked tutorial from traces to a regression suite
- [Design-to-code factory: implementation plan (MAF)](https://claude.ai/artifact/Dz3GvCzMXrkPRLubyUXHrz): the systems, registry and ledger this bible plugs into
