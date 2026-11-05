namespace Factory.Core

/// What a model can do beyond plain text.
type Capability =
    | ToolUse
    | Vision
    | LongContext
    | StructuredOutput

/// How sensitive a task's data is. A model is approved for a set of these.
type DataClass =
    | Public
    | Internal
    | Regulated

/// The two Vertex paths `ModelGateways` already has.
type Protocol =
    | AnthropicVertex
    | VertexOpenAICompatible

/// The agent role a turn runs as. This is the stats "kind": no ticket kind exists at task level.
/// Mirrors the Harness `Roles` constants that pick models today. Other roles (diagnosis, repro, ux-judge)
/// stay on env-var models until a later phase.
/// Qualified access is required: `TaskState.Phase` already has `Implementer` and `TestAuthor` cases.
[<RequireQualifiedAccess>]
type Role =
    | Planner
    | TaskPlanner
    | TestAuthor
    | Implementer
    | Reviewer
    | Qa

/// Rough task size. The caller decides the bucket; the router only keys stats by it.
type SizeBucket =
    | S
    | M
    | L

/// Price per million tokens. Cache prices come with the APIM import.
type Price = { InPerMTok: decimal; OutPerMTok: decimal }

/// One row of the model catalog.
type ModelEntry =
    { Id: string
      /// Model family, e.g. "claude" or "gemini". Used by the reviewer independence rule.
      Family: string
      Protocol: Protocol
      ModelName: string
      Tier: ModelTier
      Price: Price
      Caps: Set<Capability>
      ContextWindow: int
      ApprovedFor: Set<DataClass>
      /// Pseudo-counts (successes, failures) from benchmarks or a short eval, added to learned counts so a new
      /// model is neither blank nor perfect. (1.0, 1.0) when unknown.
      Prior: float * float }

/// What the router needs to know about one task.
type TaskSpec =
    { TaskId: string
      Role: Role
      Repo: string option
      Needs: Set<Capability>
      Risk: RiskLevel
      DataClass: DataClass
      Size: SizeBucket
      ContextTokens: int
      /// Used for expected cost when stats have too few samples.
      EstTokensIn: int
      EstTokensOut: int
      /// The reviewer must differ from the implementer's family.
      ExcludeFamilies: Set<string> }

/// Spend state of the item. Computed elsewhere; an input here.
/// Qualified access is required: an unqualified `Ok` would shadow `Result.Ok` in every later file of this namespace.
[<RequireQualifiedAccess>]
type BudgetState =
    | Ok
    | Soft
    | Downgrade
    | Blocked

type RoutingPolicy =
    { /// A function so a later phase can load it from config.
      MinTier: RiskLevel -> ModelTier
      /// Below this many samples, expected cost uses the TaskSpec estimates.
      MinSamplesForMeans: int }

type StatsKey =
    { Model: string
      Role: Role
      Repo: string option
      Size: SizeBucket }

type Counts =
    { Successes: float
      Failures: float
      MeanTokensIn: float
      MeanTokensOut: float
      Samples: int }

type Stats = Map<StatsKey, Counts>

/// One catalog model per tier. All three tiers are always present.
type Ladder = Map<ModelTier, ModelEntry>

type Ineligible = { Model: string; Reasons: string list }

type RouteOutcome =
    | Routed of ladder: Ladder * warnings: string list
    | NoEligibleModel of Ineligible list
    | NeedsDecision of reason: string
    | BudgetExceeded

[<RequireQualifiedAccess>]
module Router =
    /// Small < Standard < Frontier. Written out so it does not depend on declaration order.
    let tierRank (t: ModelTier) : int =
        match t with
        | Small -> 0
        | Standard -> 1
        | Frontier -> 2

    /// The default policy. The real values are an open decision; they live only here.
    let defaultPolicy: RoutingPolicy =
        { MinTier =
            function
            | Low -> Small
            | Medium -> Standard
            | High -> Standard
          MinSamplesForMeans = 5 }

    /// The hard filter. Collects every reason a model fails, not just the first.
    let eligible (policy: RoutingPolicy) (budget: BudgetState) (task: TaskSpec) (m: ModelEntry) : Result<unit, string list> =
        let minTier = policy.MinTier task.Risk
        let reasons =
            [ if not (Set.isSubset task.Needs m.Caps) then
                  let missing = Set.difference task.Needs m.Caps |> Set.toList |> List.map (sprintf "%A")
                  "missing capability: " + String.concat ", " missing
              if m.ContextWindow <= task.ContextTokens then
                  $"context window {m.ContextWindow} does not exceed task context {task.ContextTokens}"
              if not (Set.contains task.DataClass m.ApprovedFor) then
                  $"not approved for data class {task.DataClass}"
              if Set.contains m.Family task.ExcludeFamilies then
                  $"family {m.Family} is excluded"
              if tierRank m.Tier < tierRank minTier then
                  $"tier {m.Tier} is below the minimum {minTier} for risk {task.Risk}"
              if budget = BudgetState.Downgrade && tierRank m.Tier > tierRank Standard then
                  "budget is in downgrade: tier capped at Standard" ]
        match reasons with
        | [] -> Ok()
        | rs -> Error rs
