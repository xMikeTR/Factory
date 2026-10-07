namespace Factory.Core

// ---------- identifiers ----------

type TaskId =
    | TaskId of string
    member this.Value = let (TaskId v) = this in v

type ElementId =
    | ElementId of string
    member this.Value = let (ElementId v) = this in v

type DecisionId =
    | DecisionId of string
    member this.Value = let (DecisionId v) = this in v

type ModelTier =
    | Frontier
    | Standard
    | Small

type RiskLevel =
    | Low
    | Medium
    | High

// ---------- handover package (System 1 -> System 2 contract) ----------

/// A C4 element as System 2 sees it: where its code lives and how risky it is to change.
/// CodeAreas and Risk come from the element's d.* properties in the model.
type ElementSpec =
    { Id: ElementId
      Name: string
      CodeAreas: PathPattern list
      Risk: RiskLevel }

type DecisionCheck = { Kind: string; Run: string }

type BoundDecision =
    { Id: DecisionId
      Statement: string
      Binds: ElementId list
      Checks: DecisionCheck list }

type Handover =
    { Id: string
      Digest: string
      Elements: ElementSpec list
      Decisions: BoundDecision list
      /// Paths no task may write: the registry, CI config, verify scripts, etc.
      ProtectedPaths: PathPattern list
      /// P event names that code must log (the logging contract PObserve checks).
      EventVocabulary: string list }

// ---------- plan (planner agent output, validated before anything runs) ----------

type AutoVerifyKind =
    /// Holds regardless of shared state (status codes, schema validity, pure tests).
    | Invariant
    /// Creates its own state and asserts only on it; must clean up.
    | SelfSeeded

type AutomatedVerify =
    { Name: string
      Kind: AutoVerifyKind
      Run: string
      Cleanup: string option
      TimeoutSeconds: int }

type ManualVerify = { Name: string; Instructions: string }

type VerifyEntry =
    | Automated of AutomatedVerify
    | Manual of ManualVerify

    member this.Name =
        match this with
        | Automated a -> a.Name
        | Manual m -> m.Name

type PlannedTask =
    { Id: TaskId
      Title: string
      Instructions: string
      Elements: ElementId list
      Writes: PathPattern list
      DependsOn: TaskId list
      Covers: DecisionId list
      Verify: VerifyEntry list }

type Plan =
    { HandoverId: string
      HandoverDigest: string
      Tasks: PlannedTask list }
