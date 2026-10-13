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
      Risk: RiskLevel
      /// False for an element the item handover carries as context only: known to drift and relationships,
      /// never linked by a task and never written. True for every element of a design handover.
      InScope: bool }

type DecisionCheck = { Kind: string; Run: string }

type BoundDecision =
    { Id: DecisionId
      Statement: string
      Binds: ElementId list
      Checks: DecisionCheck list }

/// A modelled dependency: `From` may call or reference `To`. Anything else crossing between two elements'
/// code areas is a fitness violation. Directed.
type Relationship = { From: ElementId; To: ElementId }

/// A generated acceptance spec frozen into the handover (`tests/acceptance/<element>/<AC-id>.spec.ts`),
/// with the hash Designc recorded for its text.
type AcceptanceSpec = { Path: string; Hash: string }

type Handover =
    { Id: string
      Digest: string
      Elements: ElementSpec list
      Decisions: BoundDecision list
      /// Paths no task may write: the registry, CI config, verify scripts, etc.
      ProtectedPaths: PathPattern list
      /// P event names that code must log (the logging contract PObserve checks).
      EventVocabulary: string list
      /// The model's relationships between elements; empty for a handover frozen before they were carried.
      Relationships: Relationship list
      /// The frozen acceptance specs; empty for a handover frozen before specs were generated.
      AcceptanceSpecs: AcceptanceSpec list }

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
      /// Production paths: only the Implementer writes here.
      Writes: PathPattern list
      /// Test paths: only the TestAuthor writes here. Disjoint from Writes.
      TestWrites: PathPattern list
      DependsOn: TaskId list
      Covers: DecisionId list
      Verify: VerifyEntry list }

type Plan =
    { HandoverId: string
      HandoverDigest: string
      Tasks: PlannedTask list }
