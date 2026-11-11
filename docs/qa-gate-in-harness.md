# Design brief: QA gate as a native capability of my harness (System 1/2)

**For:** the Claude session working on my own agent harness ("System 2").
**From:** Mike.
**Status:** design and first slice. Read the existing harness code before proposing anything, and fit this into what's there. Don't restructure the harness to suit this brief.

---

## Background

At work I built a "QA gate" kit on top of Claude Code. A Jira ticket can only reach *Ready for QA* with proof:
- every acceptance criterion (AC) is tested, with video,
- visuals match approved baselines,
- mutation testing proves the tests actually catch bugs,
- an anti-gaming guard is clean (no skipped tests, removed assertions, or edited baselines, fixtures or ACs),
- an independent reviewer agent passes,
- and I approved the CI run.

In Claude Code this is enforced from the outside:
- an `Evidence` type with a private constructor,
- PreToolUse hooks: one only allows the Jira MCP "Ready for QA" transition after checking Azure DevOps for an approved run of that exact commit; one keeps a legacy repo read-only,
- permission deny rules that stop the agent editing gate files.

**Goal:** make this pattern native to my harness. Proof-carrying task completion and policy-enforced tool calls become harness primitives, not hooks bolted on afterwards.

## Constraints (non-negotiable)
- F#, on Microsoft Agent Framework, as the harness already is. Models via Vertex Model Garden.
- Model-provider agnostic. No LiteLLM.
- Must suit an enterprise, regulated setting: every decision auditable.
- Deterministic checks first; models only where judgement is genuinely needed.
- Governance uses my mlang/ACS work: mlang declares policies, the ACS runtime evaluates compiled manifests.
- Test-first.

---

## Concepts to implement

### 1. Proof-carrying completion
A task cannot finish as "done" without a proof value that only the harness can construct.

```fsharp
type CheckResult =
  { Name: string
    Passed: bool
    Detail: string
    Artifacts: Uri list }        // videos, reports, logs

type ProofBundle =
  { Subject: string              // e.g. ticket key
    Commit: string
    Checks: CheckResult list
    Approvals: Approval list }

type Proof = private Proof of ProofBundle

module Proof =
  /// The ONLY way to obtain a Proof. Fails if any required check is missing or failed.
  let tryCreate (required: string list) (bundle: ProofBundle) : Result<Proof, string list> = ...
  let value (Proof b) = b

type Outcome =
  | Done of Proof
  | Blocked of reasons: string list
  | NeedsHuman of question: string
```

- The agent loop's completion path takes an `Outcome`. There is no "done" without `Proof`.
- Which checks are *required* comes from policy (see 2), not from the agent.

### 2. Policy-enforced tool calls (mlang → ACS)
Every tool call passes through the ACS runtime before it executes. The Claude Code hooks become declarative policies, for example:
- `jira.transition(ticket, ReadyForQA)` requires a `Proof` for `ticket` at the current pushed commit, and that `Proof` includes an approval from Mike.
- `jira.transition(ticket, InProgress)` is allowed.
- Any other `jira.transition`, or `jira.edit`, is denied: those are human decisions.
- `fs.write` or `shell` under the legacy repo path is denied; reads are allowed.
- `fs.write` to gate files (gate config, baselines, AC snapshots, fixtures) is denied.

Requirements:
- Express these in mlang, compile them to manifests, and have ACS evaluate them on each call. If mlang can't express something yet, note the gap rather than hard-coding around it.
- Every evaluation is logged: tool, args (redacted where needed), policy, decision, reason.
- Denials go back to the agent as structured feedback it can act on. They are not crashes.

### 3. System 1 / System 2 split
| Layer | What runs there | Examples |
|---|---|---|
| Deterministic | Pure code, always first | Guard rules, test results, mutation score, CI status, policy evaluation |
| System 1 (small, cheap models) | Fuzzy but bounded classification | "Does this diff look like test-gaming?", "Is this failure missing-proof or a real bug?", "Which category is this raised issue?" (requirement change / ticket gap / legacy behaviour / ours), "Does this draft AC add anything the ticket didn't say?" |
| System 2 (frontier models) | Implementation and independent review | Writing tests and code; the reviewer that assumes the code was written to game the tests |

- **Reviewer isolation is structural.** The reviewer runs on a different model than the implementer, and the harness gives it only the diff, the ACs and a read-only repo. It can't see the implementer's context. The harness guarantees this; it doesn't rely on prompt discipline.
- System 1 outputs are advisory unless a policy explicitly makes them a required check.

### 4. Telemetry and evals
Log, per task:
- every check result,
- every policy decision,
- every System 1 judgement (input hash, output, model, latency, tokens),
- every reviewer finding.

Later, join these with outcomes: QA bounces, and the category assigned to each raised issue. That join is the eval set:
- where System 1 was wrong,
- which checks never fire (cost with no value),
- which misses slipped through (they need a new check).

Use the harness's existing telemetry path. Don't add a new one.

---

## First slice (do this first, nothing more)

1. The `Proof` type, `Proof.tryCreate` and `Outcome`, with unit tests:
   - a missing required check → `Error`,
   - a failed check → `Error`,
   - all checks passed plus an approval → `Ok`,
   - `Proof` can't be constructed outside the module (a compile-time guarantee, shown by the module signature).
2. One policy, end to end through mlang → ACS: "No `ReadyForQA` transition without `Proof` for that subject and commit."
   - Tests: allowed with a matching `Proof`; denied with no `Proof`; denied with a `Proof` for a different commit; the start transition is always allowed; other transitions are denied.
3. Decision logging for that policy.
4. Run it in **shadow mode** next to the Claude Code QA gate on my real tickets. The harness evaluates and logs what it *would* allow or deny, but enforces nothing. Then compare its decisions with the kit's. They should agree.

**Done means:** the tests pass, the policy is expressed in mlang (or the gap is documented), shadow-mode decisions are logged, and I can see where they agree or disagree with the kit.

---

## Out of scope for the first slice
- System 1 classifiers.
- Reviewer isolation.
- Eval joins.
- A Jira integration beyond what the policy needs to evaluate. Stub the transition call in tests.

## Ask me before
- changing harness architecture or public types,
- adding dependencies,
- deciding how approvals are represented (my CI approval vs an in-harness approval),
- anything about where Proof artifacts are stored.
