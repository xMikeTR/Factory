# factory-s2: coding pipeline

> **Where to look:** [ARCHITECTURE.md](ARCHITECTURE.md) is the living map (container and module diagrams,
> high-centrality modules, dated change log). New here? Open [docs/guide.html](docs/guide.html). Eval method and contract: [docs/evals/](docs/evals/).

A head start on System 2 of the design-to-code factory. System 2 takes a frozen handover package
from System 1 and turns it into merged code, recording everything it does in the ledger so that
System 4 can learn from it.

```
handover.json ─► Planner ⇄ PlanValidator ─► Dispatcher ⇄ Waves ⇄ [needs-decision → System 1]
                                                │
                                   IntegrationVerify ─► Merge ⇄ [merge-approval]
```

Agents propose and deterministic code decides. Only two places call a model: the planner, and
the turn loop inside each team. Everything else is plain code, and the parts that must be
correct are written in F#.

## Status

Tests: Core 212/0 (`dotnet run --project tests/Factory.Core.Tests`),
Integration 537/0 (`dotnet test tests/Factory.Integration.Tests`). Opt-in suites (live model, Langfuse,
GitHub, Docker, PObserve) are off unless their `FACTORY_LIVE_*` / `FACTORY_TEST_DOCKER` variable is set.
Nothing paid runs without you asking; no live Jira, UX-judge, diagnosis, repro or Playwright-CLI-with-a-model run
has been made yet.

| Area | Where | State |
|---|---|---|
| Rules: paths, plan validation, dispatch, task state, merge tiers, regeneration | `Factory.Core` (F#) | Built, tested |
| MAF workflow, teams, sandbox, verify, quality gates (mutation, coverage, fitness, UI guard) | `Factory.S2`, `Factory.Harness`, `Factory.Quality.*` | Built, tested; run live on real models |
| Work intake: work items, WIP/dependency/conflict/budget scheduler, digest, S4 metrics | `Core/Work.fs`, `Scheduler.fs`, `Board.fs`, `Digest.fs`, `Metrics.fs` | Built; a person still pulls (`item_pulled` is human-only). The digest lists `item_budget` rows under Waiting, as designc does |
| Per-run registry streams and single-writer fold | `Harness/Registry/RunLedgerStream`, `LedgerFold` | Built |
| UI acceptance: frozen spec hash check, Playwright run, `ui_acceptance_failed`, repair runs, bug intake, token lint | `Core/UiAcceptance.fs`, `VerifyRunner`, `S2/Repair.cs`, `BugIntake` | Built |
| Item budget: measure, report, and refuse a new run when exceeded | `Core/ItemBudget.fs`, `ItemBudgetReport` | Built |
| Freeze-time UX review and `item_judge` (gradient, never decides Done) | `Core/UxReview.fs`, `UxReviewRunner`, `ItemJudgeReport` | Built; paid, opt-in |
| Jira mirror (one-way, drift flagged not overwritten; REST and snapshot) | `Core/JiraMirror.fs`, `JiraMirrorReport`, `JiraRestClient` | Built; stub-tested only |
| Factory evals | `docs/evals/` | Reference copies only; the suite lives on the work VM |

## CLI

```
factory-s2 validate|dispatch   --handover h.json --plan plan.json         deterministic, no agents
factory-s2 run                 --handover <h|dir> --repo . [--registry r] [--base main] [--run-id id] [--github]
                               [--since <run>] [--repair <integration_failed run> [--diagnose]]
factory-s2 rerun               --handover <h|dir> --plan p.json --task T-001 [--k 3] --repo .
factory-s2 qa                  --repo . --run-id <id>                      follow-up QA, no author
factory-s2 digest              --registry r [--last-look ms] [--wip 3] [--notify-state f]
factory-s2 pull-plan | metrics --registry r                                read-only reports
factory-s2 budget              --repo . [--registry r] [--no-report]       exit 2 when exceeded; `run` then exits 4
factory-s2 parity-decisions    --registry r                                needs_decision per failed parity scenario; exit 2 when raised
factory-s2 intake              --registry r --id BUG-1 --spec ... --origin ... --ref ...
factory-s2 jira-mirror         --registry r [--snapshot jira.json | JIRA_URL/JIRA_TOKEN] [--apply]
factory-s2 ux-review           --repo . --handover h [--judge [--item W-1 --registry r --threshold 3.5]]
factory-s2 repro               --repo . --summary "..."                    paid, opt-in
factory-s2 compare-browser     --repo . --runs a,b                         CLI vs MCP browser tool
```

Per-command flags are the header comment of `src/Factory.S2/Program.cs`. S2 conforms to Designc's published
contracts (`ledger_schema.json`, `elements.json` /1 and /2, the work-file format) and never edits them; the copies
under `tests/Factory.Integration.Tests/Fixtures/designc/` are contract-tested.

## Layout

```
src/Factory.Core/        F#: the rules. No dependencies beyond FSharp.Core.
  Paths.fs               PathPattern: safe globs; match, overlap (errs toward "may overlap"), containment (errs toward "not inside")
  Domain.fs              handover, plan and verify types
  PlanValidator.fs       17 rules; ValidatedPlan can only be created here
  Dispatcher.fs          overlapping writes → teams; cycles between teams → merged; team DAG → waves
  TaskState.fs           task lifecycle + budgets; Passed requires PassedEvidence; replay from ledger
  Merge.fs               merge tiers, hard blocks (scope leak, protected path, manual check), enforcement mode
  Json.fs                plan / handover / dispatch / task-event codecs (collects every error)
  Interop.fs             C#-friendly facade: Core.*
tests/Factory.Core.Tests dependency-free test runner (`dotnet run`); port to xUnit when convenient
src/Factory.Harness/     C#: shared by all agent hosts
  FactoryAgents.cs       every agent built here: gateway client + policy/ledger middleware per tool call
  ScopedWorkspace.cs     agent tools; refuses writes outside the task's `writes` itself
  ToolPolicy.cs          IToolPolicy + role allow-list (in-code double)
  Governance/AcsToolPolicy.cs  IToolPolicy over AGT's PolicyEngine; one policies/s2/<role>.yaml per role
  Configurator.cs        IConfigurator + heuristic default (Jev / Laya slot in behind it)
  Ledger.cs              LedgerEvent, ILedger, JSONL implementation
  ProcessRunner.cs       shell commands with timeouts and process-tree kill
src/Factory.S2/          C#: the MAF workflow
  Workflow.cs            graph (S2Workflow) and host (WorkflowHost), the MAF run-time surface
  Executors/             Planner, PlanValidator, Dispatcher, Waves, IntegrationVerify, Merge, Finalize
  Teams/TeamRunner.cs    per-team loop: configure → plan/act/escalate → scope check → verify
  Teams/Briefs.cs        turn briefs, rebuilt from plan + handover + ledger every turn
  Git/GitWorkspace.cs    integration branch, one worktree per team, serialised idempotent merges
  Verify/VerifyRunner.cs verify blocks; cleanup always runs; a failed cleanup counts as a failure
  Integrations.cs        GitHub (gh CLI) / dry-run PRs; console human channel
prompts/                 planner, task-planner and executor system prompts (reviewed like code)
schemas/                 plan and handover JSON Schemas
sql/ledger.sql           Postgres ledger + System 4 starter views
examples/pricing-v3/     Quote lost-update handover and a valid plan
```

## Quick start

```bash
# 1. The core, no agents or network needed
dotnet run --project tests/Factory.Core.Tests            # all passed

# 2. Deterministic CLI against the example (after the S2 project compiles)
dotnet run --project src/Factory.S2 -- validate --handover examples/pricing-v3/handover.json --plan examples/pricing-v3/plan.json
dotnet run --project src/Factory.S2 -- dispatch --handover examples/pricing-v3/handover.json --plan examples/pricing-v3/plan.json
#   wave 0: team-T-001 [T-001, T-004]      (both write under src/Quotes.Data)
#   wave 1: team-T-002 [T-002], team-T-003 [T-003]   (parallel)

# 3. A real run in a target repo (PRs are written to .factory/runs/<id>/ unless --github)
export FACTORY_GATEWAY_URL=https://<apim>/litellm/v1 FACTORY_GATEWAY_KEY=<per-user key>
export FACTORY_MODEL_FRONTIER=<alias> FACTORY_MODEL_STANDARD=<alias> FACTORY_MODEL_SMALL=<alias>
#   or the native Anthropic client (public API unless FACTORY_GATEWAY_URL is set; models default
#   to claude-opus-5-5 / claude-sonnet-5-5 / claude-haiku-4-5):
#   export FACTORY_PROVIDER=anthropic ANTHROPIC_API_KEY=<key>
#   or Claude on Vertex AI with gcloud Application Default Credentials (no key, no gateway):
#   export FACTORY_PROVIDER=vertex FACTORY_VERTEX_PROJECT=<project> [FACTORY_VERTEX_REGION=global]
#   (any Vertex model: claude-* via the Anthropic client; others, e.g. google/gemini-2.5-pro, via Vertex's
#   OpenAI-compatible endpoint, so the reviewer can be a Gemini on the same login)
#   The reviewer and QA run on another model family (FACTORY_REVIEWER_PROVIDER / _GATEWAY_URL /
#   _GATEWAY_KEY / _MODEL_*, and declare FACTORY_MODEL_FAMILY + FACTORY_REVIEWER_MODEL_FAMILY); start-up
#   refuses otherwise. With one provider only: FACTORY_REVIEWER_SAME_FAMILY=allow, dry-run merges only
#   (not --github); those reviews are stamped same-family.
#   Agent contracts: a handover frozen with compiled contracts (designc `contracts/`) lists them;
#   FACTORY_CONTRACTS=warn (default) names agents with none, require refuses to start, off ignores.
#   S2 reads and checks them only; enforcing the policy at run time is not built yet.
dotnet run --project src/Factory.S2 -- run --handover <registry>/handovers/pricing-v3/handover.json \
    --repo <target repo> --registry <registry repo> [--base main] [--github] [--run-id <id to resume>]
```

A brand-new project needs no repository first: when `--repo` is missing or an empty folder, `run`
creates it as a git repository with one empty baseline commit on the base branch (a repository
with no commit gets the same baseline). A folder that holds files but is not a git repository is
refused and left untouched.

Every run leaves `.factory/runs/<id>/result.json` (`run`, `status`, `reason`, `exitCode`, `at`) saying
how the last attempt ended, on every exit path: the workflow's own status (`merged`, `parked`,
`failed`, `integration_failed`, `error`, ...), `refused` (budget, nothing to repair), `no_result`,
or, with `exitCode` null, `rejected` (a deterministic check), `cancelled` (Ctrl+C) and `failed` (a
crash, with the exception type). A resume replaces it.

The key never reaches a sandbox, a ledger row or a printed `GatewayOptions` (its `ToString`
redacts it). Opt-in live check on real Claude models (Sonnet plans, Haiku acts; usage tokens are
ledgered and the key is absent from the ledger and the state dir):
`FACTORY_LIVE_ANTHROPIC=1 ANTHROPIC_API_KEY=<key> dotnet test tests/Factory.Integration.Tests --filter LiveAnthropic`.

Turn routing: by default `HeuristicConfigurator`. With `FACTORY_LAYA_URL` set (run
`python3 tools/laya-sidecar/server.py`), Laya decides when its confidence is at least
`FACTORY_LAYA_MIN_CONFIDENCE` (default 0.7); if the sidecar is down, unsure or garbled, the
heuristic decides. Each `configurator` ledger row names the decider and why. Laya's own call is
bound in one function, `tools/laya-sidecar/laya_adapter.py`; until then the sidecar answers 503.
Sidecar tests: `cd tools/laya-sidecar && python3 -m unittest test_server`.

Traces: a run is one OTel trace (`run <id>`, then `task <id>` per task, then a gen_ai `chat`
span per model call and an `execute_tool` span per tool call); every ledger row carries its
`trace_id`. Spans are exported only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, or when `LANGFUSE_HOST`, `LANGFUSE_PUBLIC_KEY` and `LANGFUSE_SECRET_KEY` are all set (the endpoint, protocol and auth header are then derived; an explicit endpoint wins; export them in your shell profile so every run has them) (plus
`OTEL_EXPORTER_OTLP_PROTOCOL` / `OTEL_EXPORTER_OTLP_HEADERS`), to Langfuse or any OTLP collector:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:3000/api/public/otel OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Basic $(printf %s "$LANGFUSE_PUBLIC_KEY:$LANGFUSE_SECRET_KEY" | base64 -w0)"
```

The attribute contract (`Factory.Harness/Telemetry/FactoryTelemetry.cs`): `factory.system`,
`factory.run_id`, `factory.team`, `factory.task`, `factory.element[]`, `factory.decision[]`,
`factory.attempt`, the score `factory.score.task_passed` (1/0, also a `score` span event), and
Langfuse's `langfuse.trace.tags=[designc]` and `langfuse.session.id=<run id>`. Langfuse does not
take scores over OTel; post `task_passed` to its scores API if you want it there as a score.
Prompts and replies stay off spans unless `FACTORY_OTEL_CAPTURE_CONTENT=1`.
Opt-in check against langfuse-local (v4; read back through `/api/public/v2/observations`):
`. ~/Desktop/Coding/langfuse-local/env.sh; FACTORY_LIVE_LANGFUSE=1 dotnet test tests/Factory.Integration.Tests --filter LiveLangfuse`.

The target repo needs `.factory/` in its `.gitignore`, and build/test outputs such as
`artifacts/` ignored as well. Otherwise the scope check will see them as changes outside the
task's writes and revert them. Other settings (`FACTORY_MAX_TURNS`, `FACTORY_COMMANDS`,
`FACTORY_POBSERVE_CMD`, …) are listed in `FactorySettings.FromEnvironment`.

Commands run in a per-team Docker container (`FACTORY_SANDBOX=docker`, the default) built
from `docker/runner` with Podman (default) or Docker (`FACTORY_CONTAINER_ENGINE=docker`): `docker/runner/build.sh` (image
`factory-s2-runner`, override with `FACTORY_RUNNER_IMAGE`). The worktree is mounted at its own
path and the container runs as you with `--network none`, no capabilities, and memory/CPU/pid
limits, and never gets a network. `dotnet restore` runs at start in a separate short-lived container on the bridge that shares the worktree and NuGet cache (ledgered at stage
`restore`), once per project file on disk, from nuget.org only. Before it opens, every restore input
changed since HEAD is checked against an allow-list (packages, project references, compile items,
plain properties); anything else (targets, imports, custom SDKs, nuget.config, .props/.targets)
refuses the restore, which is ledgered as `refused` and goes back to the agent with the reasons. `FACTORY_SANDBOX=local` runs on the host with a scrubbed environment instead. `FACTORY_SANDBOX` is an ordered list, `docker,local`: the first that starts is used and each one skipped is ledgered as `sandbox_skipped` with its reason. Unset means `docker` only; `local` (unisolated) runs only when you name it.

Idle handoff: the prompt cache lives five minutes from its last use. If one tool call takes longer than `FACTORY_IDLE_HANDOFF_SECONDS` (default 200, 0 turns it off; a queue wait for a heavy-command slot counts), the turn ends there without being declared done, its turn row says `handoff: idle`, and the next turn starts a fresh agent from the brief with a note that the edits are on the worktree. That costs one small brief instead of resending the whole conversation cold. Turn rows also say `cold: true` when a turn that should have read the cache did not.
An agent's `run_command` gets at most 3 minutes in total (`ScopedWorkspace.DefaultCommandCap`), waiting for a free heavy-command slot and a flaky re-run included. Anthropic's prompt cache lasts 5 minutes from the last model call, and a longer tool call would make the next call pay for the whole conversation again. Longer checks belong to the verify gate, which runs between agent turns.
Docker contract tests are opt-in: `FACTORY_TEST_DOCKER=1 dotnet test --filter DockerSandbox`.

PObserve runs as an integration verify step: the runner image carries the PObserve CLI (built
from P at a pinned commit; it is not published) and `pobserve-check <specs-dir> <Spec> <logs>`,
which compiles the `.p` files in pobserve mode with the factory's generic parser
(`tools/pobserve-parser`). The logging contract, stated in every brief that has an event
vocabulary, is one line per event containing `pobserve <event> <payload> key=<id>` (scalar payload or
none; other text on the line is ignored; each key is checked as its own history), e.g.
`INFO pobserve eCommitted 3 key=Q-42`. Point the step at
the handover's behaviour specs, e.g.
`FACTORY_POBSERVE_CMD="pobserve-check /path/to/handover/behaviour NoLostUpdate artifacts/test-logs"`
(the path must be inside the sandbox's worktree mount when `FACTORY_SANDBOX=docker`). Exit 2 is
a spec violation, 4 a malformed event line. Tests: `FACTORY_TEST_DOCKER=1 dotnet test --filter PObserve`.

How reliably do the agents get a task to Passed? `rerun` makes k fresh attempts at one task,
each its own run (`rerun-<task>-<time>-k<n>`: an integration branch from `--base` and a fresh
worktree), through the same task loop as `run`. Nothing is merged and no PR is opened:

```bash
dotnet run --project src/Factory.S2 -- rerun --handover <handover> --plan plan.json --task T-001 --k 3 --repo <target repo>
#   rerun-T-001-…-k1: passed, 3 turns, tokens 600/120
#   …
# T-001 k=3: passed 2/3 (67%), turns mean 3.0, tokens in/out mean 500/100
```

Outcomes are `passed`, `failed` or `blocked` (a needs-decision; reruns never answer one).
The tasks it depends on are not replayed, so point `--base` at a branch that has their work.

Regeneration drill: change a decision in Designc, re-freeze the handover, and run again
`--since` the run that built the previous freeze, from a base that has its merged work:

```bash
dotnet run --project src/Factory.S2 -- run --handover <re-frozen handover> --repo <target repo> --since <previous run id>
```

The run reuses the previous plan (re-stamped with the new freeze and re-validated; if it no
longer fits, the planner redrafts). `Regeneration.between` diffs the bound decisions: a task
re-runs if it covers a changed, added or removed decision, or touches an element one binds
(before or after). Every other task is ledgered `carried` and not run. The `regenerate` plan row
and `.factory/runs/<id>/regeneration.json` record the diff. Integration verify still runs the
whole suite. Element-only changes (code areas, risk) and the dependents of a re-run task do not
trigger a re-run.

The merge policy comes from the target repo's `.factory/policy.yaml` (read from the base
checkout, never a task worktree):

```yaml
merge:
  enforcement: auto            # default; auto = merge on green, tiered = the tier decides approvals
  approvers: [alice, bob]
  autoMergeMaxRisk: low
  autoMergeMaxChangedLines: 400
```

`FACTORY_MERGE_ENFORCEMENT` and `FACTORY_MERGE_APPROVERS` (comma-separated) override the file.
Blocks (a scope leak, a protected path changed, a pending manual check) stop the merge in both
modes; the run ends `merge_blocked` with the blocks in the PR body.

In tiered mode with `--github`, approvals are PR comments from the allow-listed `approvers`:
`/approve <reason>` (one per approver; a lone approver meets two approvals with two distinct
reasons, recorded `single_operator: true` on the Merge ledger row) and `/verified T-001: <check>`
to record a manual check as done. Too few approvals parks the run (`parked: awaiting_approval
<url>`, exit 3); re-run with `--run-id` after commenting. Without `--github` the console asks.

Opt-in live test (opens and closes a real PR):
`FACTORY_LIVE_GITHUB=xMikeTR/pricing-sample FACTORY_LIVE_APPROVER=<login> dotnet test tests/Factory.Integration.Tests --filter LiveGitHub`

## Interfaces with designc

- **`handover.json`** (`schemas/handover.schema.json`):
  - each element needs `codeAreas` and `risk`, which means `d.code` and `d.risk` on the elements
    in the model;
  - `d.code` patterns mean what `PathPattern` means, and designc checks them before the freeze
    (DC000 for a pattern `create` rejects or a missing `d.risk`, DC009 for areas that `mayOverlap`
    or nest). designc owns the cases in its `docs/path-patterns.json`; the copy in
    `tests/Factory.Integration.Tests/Fixtures/designc/` runs against `PathPattern` here
    (`DesigncPathPatternContractTests`). Change the meaning on both sides and the fixture together;
  - decisions carry their `checks`;
  - `protectedPaths` must include the verify scripts, so the agents can run them but never edit
    them.
- **Needs-decision** payload: `BlockedTask` / `NeedsDecisionResponse` in `Messages.cs`. An
  answer binds a new decision id or abandons the task.
  - An agent raises a question with at least two options (`id` A, B…, `summary`, `consequence`) and
    an optional `recommended` id; `raise_needs_decision` refuses anything less.
  - The registry `needs_decision` row carries them in `payload.options` / `payload.recommended`.
  - designc answers with `payload.option` (the chosen id) and/or `payload.note` on
    `needs_decision_resolved`. An id that was never offered answers nothing: the run stays parked
    and the ledger records `resolution_rejected`.
- **Ledger kinds** (`LedgerKinds`) and the `task` event payload (`Json.encodeTaskEvent`). System 4
  queries depend on these.

## Design decisions

- **The team loop is a plain loop over the F# state machine, not a nested MAF workflow.** There
  are three reasons. The number of teams is only known after dispatch. MAF runs executors in
  supersteps with a barrier between them, so a slow team would hold up the others. And the
  ledger already makes the loop resumable. MAF still owns the outer flow and the human-in-the-loop
  ports.
- **Resuming reruns the idempotent executors against the same run id.** Each executor re-reads
  the plan and replays the ledger, rather than depending on MAF checkpoints, and every resume
  re-validates the plan.
- **Each turn gets a fresh brief, never a chat history.** The approach note, the previous verify
  failures and any newly bound decision are carried between turns through the ledger.
- **Signals come through tools, not free text.** `declare_done` and `raise_needs_decision` are
  tool calls, so they are governed and logged like any other call. The planner's plan is too:
  `submit_plan` takes the plan schema as its input schema and answers with the validator's errors,
  so the planner fixes a rejected plan within the same turn.
- **Scope is enforced three times:**
  - the workspace tools refuse writes outside the task's paths;
  - a git check after every turn reverts out-of-scope changes and logs a `scope_violation`;
  - a changed file outside the declared writes, or under a protected path, blocks the merge in every mode.
- **Self-seeded verify steps get `FACTORY_VERIFY_NS`,** so seeded data never collides between
  runs or attempts.
- **The only change to the task-state DU is `Ready of Progress`.** The Queued state
  becomes `Ready {0;0}`, so that turn and verify budgets survive escalation and resume.

## CI for heavy suites, then a bug-fix run

Keep the pre-commit checks under 3 s (`check_quality`) and run long suites in CI, so the run ends instead of waiting on them. When CI fails, record it against the run:

```
factory-s2 ci-report --repo . --run-id <id> --check "<CI check>" --log ci.log [--file a.cs,b.cs | --task T-001] [--commit <sha>]
factory-s2 run --handover <handover> --repo . --repair <id>
```

`ci-report` blames the tasks whose `writes` or `testWrites` match the failing files (or the task you name) and writes one `ci_failed` ledger row each, carrying only the log tail. Nothing is guessed: if no task owns the files it says so. `--repair` then re-runs only the blamed tasks from the run's integration branch, with the CI failure as their brief. A chain of repairs stops after `FACTORY_MAX_FIX_ROUNDS` rounds (default 2) and asks for a person.

To measure it: compare tokens per passing task from `by_role` for a run that ran the suite itself against one that offloaded it to CI plus its repair round.
