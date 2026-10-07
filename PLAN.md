# Plan — System 2 coding pipeline

> Source: /grill-me session (2026-10-07). Baseline map: `ARCHITECTURE.md`.
> Scope file: `.claude/current-plan-scope.json` (ephemeral).

## Goal

Take a frozen designc handover and produce merged code in a target repo. Agents write tests and code. Deterministic F# decides pass, scope and merge. Evidence goes to designc's ledger and to Langfuse.

**Success:**
1. The workbook definition of done is met: 30 Core tests green, `validate`/`dispatch` work, T-001 dry-run ledger replays to `Passed`, tokens on every turn, a verify row per attempt, the resume drill works, and a denied tool call is ledgered.
2. A designc handover runs end to end with two separate roles (TestAuthor, then Implementer). Pass needs three gates: a red run, green, and the stashed-impl check. P2P regressions are blocked.
3. The run produces designc ledger rows that pass `designc` validation, and Langfuse traces that carry the agreed attributes.
4. Agent code runs only in Docker. Secrets never reach containers, ledgers, briefs or traces.

**Constraints:**
- TDD on every step.
- Domain types stay pure F#, and adapters sit at the edge.
- Designc is canonical.
- No Docker Desktop.
- APIM owns budgets at work; locally, only the F# turn and verify budgets apply.

**Out of scope:** Postgres ledger, MAF checkpointing, the mlang compiler, the TDAD impact graph, designc's d.code layout check (System 4), the real 11 tasks, and GitHub branch-protection enforcement.

## Module Impact

| Module | Change | Blast radius |
|---|---|---|
| `Core/Paths, PlanValidator, Dispatcher, TaskState, Merge` | Fill 8 TODOs (no interface change) | none, tests already exist |
| `Core/Domain.fs` ⚠ high-centrality | `PlannedTask.TestWrites` | PlanValidator, Dispatcher, Merge, Json, schema, S2 Briefs |
| `Core/TaskState.fs` ⚠ | `Phase` in Running/Verifying; hardened `PassedEvidence` | Json codec, TeamRunner, Configurator, tests |
| `Core/PlanValidator.fs` ⚠ | +3 `PlanError` cases | Interop error rendering |
| `Core/Merge.fs` | `MergeEnforcement`, `MergeBlock` | MergeExecutor |
| `Core/Regression.fs` **new** (after TaskState) | pure P2P comparison | VerifyRunner |
| `Harness/FactoryAgents.cs` | `GatewayOptions.Provider`; implement middleware | every agent |
| `Harness/Sandbox/` **new** | `ICommandSandbox`, Local, Docker | VerifyRunner, ScopedWorkspace, TeamRunner |
| `Harness/Governance/AcsToolPolicy.cs` **new** | implements existing `IToolPolicy` | none (same interface) |
| `Harness/Configurator.cs` | `LayaConfigurator` + fallback composite | none (same interface) |
| `Harness/Registry/` **new** | `RegistryLedgerProjector`, `DesigncHandoverReader` | S2 Program, Finalize |
| `Harness/Telemetry/` **new** | OTel setup + attribute contract | FactoryAgents |
| `S2/*` | Fill 4 TODOs; two-phase loop; park/exit; `rerun` command | — |
| `tests/Factory.Integration.Tests` **new** (C#, xUnit) | Harness and S2 tests | — |
| `tools/laya-sidecar`, `tools/pobserve-parser`, `docker/runner` **new** | external processes | — |
| Designc repo | `d.code`, `d.risk` in SPECS (rules.py + core.js) | designc lint only |

## Interface Changes (decided in the grill; flagged here as widenings)

```fsharp
// Domain.fs
type PlannedTask = { ...; TestWrites: PathPattern list }          // disjoint from Writes

// PlanValidator.fs: new PlanError cases
| TestWritesOverlapWrites of TaskId * PathPattern * PathPattern
| TestWriteOutsideElement of TaskId * PathPattern
| InvariantNotUnderTestWrites of TaskId * verifyName: string        // must run something under TestWrites or a protected path

// TaskState.fs
type Phase = TestAuthor | Implementer
type TaskState =
  | Ready of Progress
  | Running of Progress * Phase * ModelTier
  | Blocked of Progress * Phase * NeedsDecision
  | Verifying of Progress * Phase
  | Passed of PassedEvidence
  | Failed of FailureReason * Progress
type TaskEvent = ... | TestsAuthored of RedEvidence                 // TestAuthor -> Implementer; needs a non-zero red run
type RedKind = BuildFailure | AssertionFailure
type RedEvidence = private { Outcomes: VerifyOutcome list; Kind: RedKind }   // Create refuses all-zero
PassedEvidence.Create : attempt:int * green:VerifyOutcome list * red:RedEvidence * stashedImpl:VerifyOutcome list
                        -> Result<PassedEvidence, string>          // stashedImpl must contain a non-zero invariant

// Regression.fs (new, pure)
type TestResult = { Name: string; Passed: bool }
val regressions : baseline: TestResult list -> current: TestResult list -> string list   // passed before, fails now

// Merge.fs
type MergeEnforcement = Auto | Tiered of approvers: string list
type MergeBlock = ScopeLeak of string | ProtectedPathChanged of string | ManualVerifyPending of string
type MergeVerdict = { Tier: MergeTier; Reasons: string list; Blocks: MergeBlock list }   // any block = no merge, in every mode
```

```csharp
// Harness
public enum ModelProvider { Anthropic, OpenAICompatible }      // FACTORY_PROVIDER
public sealed record GatewayOptions(ModelProvider Provider, string? Endpoint, string ApiKey,
                                    string FrontierModel, string StandardModel, string SmallModel);
public interface ICommandSandbox : IAsyncDisposable {
    string Kind { get; }                                        // "docker" | "local"; ledgered
    Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout,
                                 IReadOnlyDictionary<string,string> env, CancellationToken ct);
}
public interface ISandboxFactory { Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct); }
public static class RegistryLedgerProjector {                  // pure; deterministic ids and ts
    public static IReadOnlyList<RegistryEvent> Project(IReadOnlyList<LedgerEvent> run, HandoverRef handover);
}
public interface IHandoverSource { Task<Handover> LoadAsync(string handoverDir, S2Config config, CancellationToken ct); }
// LayaConfigurator : IConfigurator; FallbackConfigurator(primary, fallback, minConfidence) : IConfigurator
// AcsToolPolicy : IToolPolicy (loads policies/s2/<role>.yaml; header source: hand-authored | mlang@x)
```

CLI:
- `factory-s2 run --handover <dir> --repo <path> [--run-id <id>]`. On exit it prints `parked: ND-xxxx`.
- `factory-s2 rerun --handover <dir> --task <id> --k 3`.

## Test Strategy

- **Core (F#):** add to the existing runner. Pure functions only, no mocks.
- **Integration (C#, xUnit):** the main test doubles are `ScriptedChatClient : IChatClient`, which replays a turn script, and `LocalSandbox`. Docker and live-model tests are opt-in traits (`[Trait("needs","docker")]`, `"live-model"`).
- **Top 3 behaviours:**
  1. A task reaches `Passed` only with red, green and stashed-impl evidence, and the ledger replays to it.
  2. Writes outside scope or under protected paths never merge, in any mode.
  3. Projecting the same run twice gives byte-identical designc rows, and those rows pass designc's schema.
- **Most important failure case:** an Implementer edits the tests. The tool refuses the edit, `EnforceScopeAsync` reverts it, and a `tool_call denied` row is ledgered.
- **Contract tests:** each contract test runs the same cases against both implementations:
  - Local vs Docker sandbox
  - RoleAllowList vs ACS policy
  - Heuristic vs Laya configurator (shape only)

## Step Sequence

Each step is TDD (red → green → refactor). The ⟂ marks a `/session-handoff` point.

**A. Foundations**
- **A0** — `git init` the workbook, add `Factory.sln`, retarget to net9.0, and stay on MAF 1.15 for now. Then `dotnet build`: fix compile drift in the uncompiled MAF code, with no behaviour change. *Test:* the solution builds; the Core tests run (TODO failures expected).
- **A1** — Fill the Core TODOs in work order: Paths → TaskState.transition → PlanValidator → Dispatcher → Merge.decide. *Test:* 30/30 green; `validate`/`dispatch` on `examples/pricing-v3`. ⟂

**B. Tracer bullet: T-001, scripted model, local sandbox, dry-run merge**
- **B0** — Designc repo: add `d.code` and `d.risk` to SPECS (rules.py + core.js) with tests, as a separate commit. Create the `factory-registry` fixture (sample-registry plus d.code/d.risk, frozen handover) and `pricing-sample` (sample-codebase with bin/obj stripped; local only). *Test:* `designc check` is clean on the fixture.
- **B1** — `DesigncHandoverReader` (YAML manifest + DSL element reader → F# `Handover`; check-string prefix mapping; ProtectedPaths from config). *Test:* the fixture maps to the expected `Handover`; an unknown bound element gives a typed error.
- **B2** — `ICommandSandbox` + `LocalSandbox` (scrubbed env) + `VerifyRunner.RunTaskAsync`. *Test:* cleanup always runs; a failed cleanup counts as a failure; a timeout kills the process tree; `FACTORY_GATEWAY_KEY` is absent from the child env.
- **B3** — `GovernedToolCallAsync` + `TaskLoop.RunAsync/NextTurnAsync` driven by `ScriptedChatClient`. *Test:* a denied tool produces a `tool_call/denied` row; every turn has tokens; there is a verify row per attempt; the budget is exhausted to `Failed`.
- **B4** — `S2Workflow.Build` + `run` in dry-run on T-001. *Test:* end to end, the ledger replays to `Passed` and the dry-run PR body exists.
- **B5** — Resume drill: kill mid-wave and run `--run-id`. *Test:* no duplicate commits or ledger rows; the result is the same final state. **Workbook DoD met.** ⟂

**C. Separation of duties + gates**
- **C1** — F#: `TestWrites`, the 3 PlanErrors, plus the codec and schema. *Test:* one validator test per new error.
- **C2** — F#: `Phase`, `RedEvidence`, and hardened `PassedEvidence.Create`. *Test:* the transition table includes phases; Create refuses missing red and refuses a stashed run that is all green.
- **C3** — TeamRunner in two phases: separate roles, briefs and policies; the red run; the stashed-impl check via `GitWorkspace.StashImplAsync`. *Test (scripted):*
  - an Implementer touching TestWrites is refused;
  - a test that passes against the stub never leaves TestAuthor;
  - a vacuous test fails the stash check.
- **C4** — `Regression.regressions` + an integration baseline in VerifyRunner, feeding `Briefs.Failures`. *Test:* pure cases, plus a scripted run where breaking a passing test blocks `Passed`. ⟂

**D. Ledgers, needs-decision, parking**
- **D1** — `RegistryLedgerProjector`, which writes designc streams. *Test:* byte-identical output across two projections; rows validate against designc `ledger_schema.json`; event_ids are stable.
- **D2** — `needs_decision` in pipeline.jsonl, then park and exit, then resume once `needs_decision_resolved` appears. *Test:* the run parks with `parked: ND-0001`; designc's gate is blocked; after resolution it resumes to `Passed`.
- **D3** — `handover_closed` outcome, including the abandoned rule. *Test:* abandoned unless every decision check passes. ⟂

**E. Merge**
- **E1** — `MergeEnforcement` + `MergeBlock`, and `.factory/policy.yaml` with an env override. *Test:* every block stops a merge in both modes; auto mode merges on green.
- **E2** — **Confirm with you first.** Create the private GitHub repo `xMikeTR/pricing-sample`, then push. `GhCliPullRequests` handles `/approve` from the allow-list and the single-operator override. *Test:* opt-in live PR on T-001; the tier and reasons appear in the PR body and the ledger. ⟂

**F. Real execution**
- **F1** — `docker/runner` image (`factory-s2-runner`: .NET 9 SDK, P toolchain, JDK) + `DockerSandbox` (per-team container, network none apart from the restore window, limits). *Test:* the sandbox contract tests pass; a canary secret is invisible; outbound network is refused.
- **F2** — `GatewayOptions.Provider` + Anthropic `IChatClient`. *Test:* opt-in live T-001 on Haiku/Sonnet; usage tokens are ledgered; the key is absent from ledger and traces. ⟂

**G. Governance + configurator**
- **G1** — `AcsToolPolicy` + `policies/s2/{planner,test-author,implementer,merge}.yaml`. Use the AGT package and pin the ACS beta exactly; bump MAF only if AGT forces it. *Test:* the policy contract test runs against both implementations.
- **G2** — `tools/laya-sidecar` + `LayaConfigurator` + `FallbackConfigurator`. *Test:* when the sidecar is down or confidence is low, the heuristic decides, and the ledger names the deciding configurator. ⟂

**H. Observability + behaviour**
- **H1** — OTel: the attribute contract (system/element/task/decision/attempt, tag `designc`, score `task_passed`); trace_id on ledger rows; content off by default. *Test:* an in-memory exporter asserts the contract; an opt-in run against langfuse-local.
- **H2** — `tools/pobserve-parser` (Java; `eCommitted` lines → P events), wired as an integration verify step. *Test:* a good log passes; a log that violates the spec fails. ⟂

**I. Drills**
- **I1** — `rerun --k 3`: pass rate, turns and tokens per task. *Test:* a scripted, deterministic k=3 produces the expected stats.
- **I2** — Regeneration drill: change a decision → re-freeze → run → diff. *Test:* only the bound element's tasks re-run.
- **I3** — Update ARCHITECTURE.md (diagrams, centrality, integrations) and delete the scope file.

## Risks

| Risk | Mitigation |
|---|---|
| The MAF 1.15 code has never been compiled, and AGT's extension references 1.17 | A0 isolates the compile drift; bump the version only at G1 |
| The `Phase` and `TestWrites` widenings ripple through codecs and the schema | Done in C, after the tracer is green, as separate commits |
| ACS is a beta package | Exact pin; `RoleAllowListPolicy` stays as the double under a contract test |
| `DesigncHandoverReader` parses Structurizr DSL by hand | Read only, limited to elements and properties; fixture tests; breaks loudly on unknown syntax |
| Docker network gating of NuGet restore is fiddly | Restore in a separate step with the network enabled, then run with `--network none`; ledger the restore |
| Laya confidence semantics are unknown | The fallback threshold is configurable; the heuristic is the default |
| Nested git: the workbook sits inside the un-committed `~/Desktop/Coding` repo | A0 inits its own repo; the parent `.gitignore` is untouched |
| Biggest assumption: a scripted model makes the loop testable enough | B3 proves it before any live model spend |
| Still open (work VM) | Real 11 tasks via k-rerun; ACS manifests vs mlang output; APIM cost visibility |
