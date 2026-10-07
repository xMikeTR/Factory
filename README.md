# factory-s2: System 2 (coding pipeline), workbook edition

> **This copy has 12 `TODO(you)` stubs across nine areas** in the parts worth writing yourself. Start with
> [RESOURCES.md](RESOURCES.md): it gives the work order, the spec for each stub and what to read.
> The core tests start at 5 passed / 25 failed and are your acceptance criteria.
> The status table below describes the reference implementation (the original factory-s2.zip).


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

## What is verified and what is not

| Part | Status |
|---|---|
| `Factory.Core` (F#): paths, validator, dispatcher, task state machine, merge tiers, JSON codecs | Compiles; **30 tests pass** against the Quote example |
| C# code that uses the F# types from `Factory.Core` (DU cases, records, options) | Compile-checked against the real `Factory.Core` |
| `examples/pricing-v3/*.json` | Valid against `schemas/*.json` and accepted by the validator |
| C# code that calls MAF (agents, middleware, executors, workflow, host) | Written against MAF 1.15 docs; **not compiled** (no NuGet access where it was written). Every call that depends on the MAF API version is marked `// VERIFY` |

The code that uses MAF is confined to `FactoryAgents.cs`, `Executors/*.cs` and `Workflow.cs`.
Getting those to compile is part of the Phase 0 spike.

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
dotnet run --project tests/Factory.Core.Tests            # 30 passed, 0 failed

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
dotnet run --project src/Factory.S2 -- run --handover <registry>/handovers/pricing-v3/handover.json \
    --repo <target repo> --registry <registry repo> [--base main] [--github] [--run-id <id to resume>]
```

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
`trace_id`. Spans are exported only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (plus
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
from `docker/runner` with the Docker Engine CLI: `docker/runner/build.sh` (image
`factory-s2-runner`, override with `FACTORY_RUNNER_IMAGE`). The worktree is mounted at its own
path and the container runs as you with `--network none`, no capabilities, and memory/CPU/pid
limits. The network is attached only while `dotnet restore` runs at start (ledgered at stage
`restore`). `FACTORY_SANDBOX=local` runs on the host with a scrubbed environment instead.
Docker contract tests are opt-in: `FACTORY_TEST_DOCKER=1 dotnet test --filter DockerSandbox`.

PObserve runs as an integration verify step: the runner image carries the PObserve CLI (built
from P at a pinned commit; it is not published) and `pobserve-check <specs-dir> <Spec> <logs>`,
which compiles the `.p` files in pobserve mode with the factory's generic parser
(`tools/pobserve-parser`). The logging contract, stated in every brief that has an event
vocabulary, is one line per event containing `pobserve <event> <payload>` (scalar payload or
none; other text on the line is ignored), e.g. `INFO pobserve eCommitted 3`. Point the step at
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

## Build order

This maps onto Phases 0, 2 and 4 of the implementation plan. Each step leaves you with
something that works on its own.

1. **Core in CI (day 1).** Add `Factory.Core` and its tests to the pipeline, then run
   `validate` and `dispatch` on the plans your existing impl-plan skill produces today. This
   answers the open question about whether impl-plan tasks can carry C4 element IDs and `writes`,
   using real data.
2. **Phase 0 spike: get the MAF code compiling.** Work through the `// VERIFY` markers:
   - the executor `[MessageHandler]` style and its source generator;
   - `AddEdge<T>` with conditions;
   - `RequestPort` round-trips, including `DataAs<T>`;
   - the function-invocation middleware signature.

   Tool governance is done: `AcsToolPolicy` (AGT `Microsoft.AgentGovernance` 5.0.0, exact pin)
   evaluates `policies/s2/<role>.yaml` inside the ledger middleware. Each file needs a
   `# source: hand-authored | mlang@<version>` header and `default_action: deny`; a bad file stops
   the run. `FACTORY_TOOL_POLICY=allowlist` falls back to the in-code list; `FACTORY_POLICY_DIR`
   points at mlang output. Note AGT 5.0.0 conditions: `in [..]` lists never match, so write one
   `tool_name == '...'` rule per tool.
3. **One task, one team (Phase 2 slice).** Run T-001 from the example against the real repo,
   using the dry-run PR mode. Check the ledger: task events should replay to `Passed`, and there
   should be token counts on each turn and a verify row per attempt.
4. **Resume drill.** Kill the process mid-turn and rerun with the same `--run-id`. The task
   should resume from the ledger and finished work should not be redone. Then kill it during a
   needs-decision wait and do the same.
5. **Full example.** Run all four tasks. Wave 1 runs two teams in parallel, and the PR is opened
   at the tier the F# policy chose (two approvals here: Quotes DB is high risk and T-004 has a
   manual check).
6. **Regeneration drill.** Delete `src/Quotes.Data`, rerun from the same handover, and require
   every check to pass. Record the result in the ledger.
7. **Swap in the real integrations as they land:**
   - a Postgres `ILedger`, using `sql/ledger.sql`;
   - Jev/Laya behind `IConfigurator`;
   - a registry-backed `IHumanChannel` (needs-decision minted into the registry for System 1's
     grill-me, merge approval from a GitHub webhook);
   - MAF checkpointing, once the checkpoint-store decision is made.

## Contracts to agree with System 1

- **`handover.json`** (`schemas/handover.schema.json`):
  - each element needs `codeAreas` and `risk`, which means `d.code` and `d.risk` on the elements
    in the model;
  - decisions carry their `checks`;
  - `protectedPaths` must include the verify scripts, so the agents can run them but never edit
    them.
- **Needs-decision** payload: `BlockedTask` / `NeedsDecisionResponse` in `Messages.cs`. An
  answer binds a new decision id or abandons the task.
- **Ledger kinds** (`LedgerKinds`) and the `task` event payload (`Json.encodeTaskEvent`). System 4
  queries depend on these.

## Design decisions to review

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
  tool calls, so they are governed and logged like any other call.
- **Scope is enforced three times:**
  - the workspace tools refuse writes outside the task's paths;
  - a git check after every turn reverts out-of-scope changes and logs a `scope_violation`;
  - a changed file outside the declared writes, or under a protected path, blocks the merge in every mode.
- **Self-seeded verify steps get `FACTORY_VERIFY_NS`,** so seeded data never collides between
  runs or attempts.
- **The only change to the Phase 2 task-state DU is `Ready of Progress`.** The Queued state
  becomes `Ready {0;0}`, so that turn and verify budgets survive escalation and resume.
