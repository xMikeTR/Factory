namespace Factory.Core.Gate

/// One deterministic check's verdict; Artifacts are videos, reports, logs.
type CheckResult =
    { Name: string
      Passed: bool
      Detail: string
      Artifacts: System.Uri list }

/// A person's (or CI's) sign-off. How an approval is represented beyond who and where from is
/// undecided (CI approval vs in-harness), so this is the minimum.
type Approval = { By: string; Source: string }

type ProofBundle =
    { Subject: string
      Commit: string
      Checks: CheckResult list
      Approvals: Approval list }

/// Evidence that a subject at a commit passed its required checks and was approved.
/// The constructor is hidden: Proof.tryCreate is the only way to obtain one.
type Proof

[<RequireQualifiedAccess>]
module Proof =
    /// The ONLY way to obtain a Proof. Errors name every missing or failed required check, and a missing approval.
    val tryCreate: required: string list -> bundle: ProofBundle -> Result<Proof, string list>
    val value: Proof -> ProofBundle
    /// True when the proof is for exactly this subject at exactly this commit.
    val covers: subject: string -> commit: string -> Proof -> bool

/// How a task ends. There is no Done without a Proof.
type Outcome =
    | Done of Proof
    | Blocked of reasons: string list
    | NeedsHuman of question: string
