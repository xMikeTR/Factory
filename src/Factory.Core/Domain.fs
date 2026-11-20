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
      InScope: bool
      /// The repo the element's code lives in (designc `d.repo`, elements.json `repo`); None when the design declares no repos.
      Repo: string option }

/// designc's hash16: two FNV-1a passes over the UTF-16 units, 16 hex. Fingerprints frozen files and names checks.
[<RequireQualifiedAccess>]
module Hash16 =
    let private fnv (s: string) (seed: uint32) =
        let mutable h = seed
        for c in s do
            h <- (h ^^^ uint32 c) * 16777619u
        h.ToString("x8")

    let compute (s: string) = fnv s 2166136261u + fnv s 2654435769u

/// CheckId: designc records/1's id, given ({id, run}) or derived from the command: chk- + the first 8 hex of hash16(trimmed run).
type DecisionCheck = { Kind: string; Run: string; CheckId: string }

[<RequireQualifiedAccess>]
module DecisionCheck =
    let idOf (run: string) = "chk-" + (Hash16.compute (run.Trim())).Substring(0, 8)

    let create (kind: string) (run: string) (id: string option) =
        { Kind = kind; Run = run; CheckId = id |> Option.defaultWith (fun () -> idOf run) }

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

/// A frozen acceptance criterion (handover `acceptance/<id>.yaml`): `Element` is the element it is about,
/// as Designc wrote it (a name or a `System/Container` path); `Text` is the frozen file.
type AcceptanceCriterion = { Id: string; Element: string; Text: string }

/// A read-only reference (handover.yaml `references`): the legacy app being replaced. The handover names it by key
/// and kind only; the path comes from whoever runs the build, so the handover is machine-independent.
type Reference = { Key: string; Kind: string }

/// A compiled agent contract (handover.yaml `contracts.agents`): the design agent it governs, the frozen policy
/// file inside the handover and that file's hash16. The policy is whatever the team's compiler produced; S2 does not read it here.
type AgentContract = { Agent: string; Policy: string; Hash: string }

/// A text file the handover carries for a UI screen (`ui.json` names it with its sha256): kept in the record because the run needs
/// the text after the handover directory is gone.
/// Any frozen text (UI spec, OpenAPI contract), not only UI.
type UiText = { Path: string; Sha256: string; Text: string }

/// A holdout acceptance case, named by hash only (handover `ui.json` `holdout_cases`). The case itself never reaches the Factory's run.
type UiHoldoutCase = { Criterion: string; Case: int; State: string; Sha256: string }

/// One bound UI screen of a handover (`ui.json`). `Component` and `File` are read from the spec. `Previews` is (path under
/// `ui/previews/<screen>/`, sha256); `Holdout` is the holdout sample's sha256, never its content.
type UiScreen =
    { Screen: string
      Component: string
      File: string
      Spec: UiText
      Template: UiText
      Components: UiText list
      Previews: (string * string) list
      Holdout: string option
      HoldoutCases: UiHoldoutCase list }

/// One test a handover names (`tests.json`, designc.handover.tests/1). `Command` is None for a test designc lists but cannot run.
type NamedTest = { Kind: string; Path: string; Command: string option; Via: string option }

/// The tests designc mapped to one element: the ones that must turn green (acceptance, the ui page test) and the existing ones that must stay green.
type ElementTests = { Element: string; Repo: string option; FailToPass: NamedTest list; PassToPass: NamedTest list }

/// handover.yaml `tests`: per element, plus the elements with code and no covering test (element, reason).
type TestMap = { Elements: ElementTests list; Unmapped: (string * string) list }

/// A scenario test of one screen (`scenarios.json`, designc.handover.scenarios/1). `Examples` is (index, title); `Operation` is
/// (operationId, method, path) of the request the test waits for.
type ScenarioRef = { Id: string; Operation: string * string * string; Examples: (int * string) list }

/// The generated scenario test of one screen: `Test` is designc's text, run as written and never regenerated.
type ScenarioScreen = { Screen: string; Component: string; Test: UiText; Scenarios: ScenarioRef list }

/// One end of a typed relationship, from designc's elements/3 `interfaces` (operations carry method/path/file, events carry neither).
type ElementInterface =
    { Element: string
      Role: string
      From: string
      To: string
      Kind: string
      Id: string
      Method: string option
      Path: string option
      File: string option
      Version: string }

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
      AcceptanceSpecs: AcceptanceSpec list
      /// The work item an item handover was frozen for (handover.yaml `work_item`); None for a design handover.
      WorkItem: string option
      /// The frozen design-tokens/tokens.json text (handover.yaml `tokens`); None when the design has none.
      DesignTokens: string option
      /// The design revision (`r-...`) an item handover was frozen at (handover.yaml `design_version`).
      DesignVersion: string option
      /// The frozen acceptance criteria (handover.yaml `acceptance`); empty when the handover lists none.
      AcceptanceCriteria: AcceptanceCriterion list
      /// The acceptance specs the agent's own loop runs (handover.yaml `loop_specs`): a subset of AcceptanceSpecs;
      /// the full suite runs at merge. Empty when the handover names none.
      LoopSpecs: string list
      /// Read-only references the build may read for parity and never writes; empty when the handover names none.
      References: Reference list
      /// Compiled contracts for the design's agents (handover.yaml `contracts`); empty when the handover has none.
      Contracts: AgentContract list
      /// Design agents (agents.json) that act on their own and have no compiled contract; empty when none, or when the handover lists no agents.
      Uncovered: string list
      /// The OpenAPI contract files the handover pins (handover.yaml `contracts_api`); empty when it pins none.
      ApiContracts: string list
      /// Risk the requirement declares (handover.yaml `requirementRisk`, critical reads as high); None when absent or unknown.
      RequirementRisk: RiskLevel option
      /// Data class the requirement declares (`requirementDataClass`: public|internal|regulated); None when absent or unknown.
      RequirementDataClass: string option
      /// The bound UI screens (handover.yaml `ui`); empty when the handover has none, and for a handover JSON frozen before they were carried.
      Ui: UiScreen list
      /// The test map (handover.yaml `tests`); None when the handover has none, and for a handover JSON frozen before it was carried.
      Tests: TestMap option
      /// The generated scenario tests (handover.yaml `scenario_tests`); empty when the handover has none.
      ScenarioTests: ScenarioScreen list
      /// Scenarios designc did not generate a test for (scenario, reason); empty when none.
      ScenarioSkipped: (string * string) list
      /// Every interface of every element (elements/3), in file order; empty for /1 and /2 and for older handover JSON.
      Interfaces: ElementInterface list
      /// The frozen text of each `contracts_api` file, sorted by path; empty when the handover pins none.
      ApiContractTexts: UiText list }

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
