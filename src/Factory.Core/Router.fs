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

/// Settings `Router.taskSpec` needs, loaded from the target repo's `.factory/policy.yaml` `routing:` section.
type TaskSpecConfig =
    { /// A task with at most this many writes plus test writes is size S.
      SmallMaxWrites: int
      /// Above SmallMaxWrites and at most this many is size M; more is L.
      MediumMaxWrites: int
      DataClass: DataClass
      /// Token estimates (in, out) per role, used until stats have enough samples.
      Estimates: Role -> int * int }

/// Learned pass/fail counts and token means per model, role, repo and size bucket.
[<RequireQualifiedAccess>]
module Stats =
    /// Adds one attempt: bumps the success or failure count and folds the token counts into the running means.
    let record (key: StatsKey) (passed: bool) (tokensIn: int) (tokensOut: int) (stats: Stats) : Stats =
        let c =
            Map.tryFind key stats
            |> Option.defaultValue { Successes = 0.0; Failures = 0.0; MeanTokensIn = 0.0; MeanTokensOut = 0.0; Samples = 0 }
        let n = float c.Samples
        let mean old x = (old * n + float x) / (n + 1.0)
        let updated =
            { Successes = (if passed then c.Successes + 1.0 else c.Successes)
              Failures = (if passed then c.Failures else c.Failures + 1.0)
              MeanTokensIn = mean c.MeanTokensIn tokensIn
              MeanTokensOut = mean c.MeanTokensOut tokensOut
              Samples = c.Samples + 1 }
        Map.add key updated stats

    /// Multiplies every success and failure count by the factor, so old evidence fades.
    /// Token means and sample counts are unchanged.
    let decay (factor: float) (stats: Stats) : Stats =
        stats |> Map.map (fun _ c -> { c with Successes = c.Successes * factor; Failures = c.Failures * factor })

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

    let private statsKey (task: TaskSpec) (m: ModelEntry) : StatsKey =
        { Model = m.Id; Role = task.Role; Repo = task.Repo; Size = task.Size }

    /// What one attempt is expected to cost on this model, in the price's currency. Uses the learned token means
    /// once there are enough samples, else the task's own estimates.
    let expectedCost (policy: RoutingPolicy) (stats: Stats) (task: TaskSpec) (m: ModelEntry) : decimal =
        let tokensIn, tokensOut =
            match Map.tryFind (statsKey task m) stats with
            | Some c when c.Samples >= policy.MinSamplesForMeans -> decimal c.MeanTokensIn, decimal c.MeanTokensOut
            | _ -> decimal task.EstTokensIn, decimal task.EstTokensOut
        (tokensIn * m.Price.InPerMTok + tokensOut * m.Price.OutPerMTok) / 1000000m

    /// A Thompson draw of the model's pass rate: Beta over learned counts plus the model's prior.
    /// An unknown key counts as no learned attempts, so only the prior speaks.
    let successDraw (rng: Rng) (stats: Stats) (task: TaskSpec) (m: ModelEntry) : float =
        let s, f =
            match Map.tryFind (statsKey task m) stats with
            | Some c -> c.Successes, c.Failures
            | None -> 0.0, 0.0
        let ps, pf = m.Prior
        Sampling.beta rng (s + ps + 1.0) (f + pf + 1.0)

    let private allTiers = [ Small; Standard; Frontier ]

    /// Fills every tier from the routed ones: a tier with no pick takes the next routed tier up,
    /// and above the highest routed tier it takes the highest.
    let private fill (picks: Map<ModelTier, ModelEntry>) : Ladder =
        let routed = picks |> Map.toList |> List.sortBy (fst >> tierRank)
        let highest = routed |> List.last |> snd
        allTiers
        |> List.map (fun t ->
            let pick =
                routed
                |> List.tryFind (fun (rt, _) -> tierRank rt >= tierRank t)
                |> Option.map snd
                |> Option.defaultValue highest
            t, pick)
        |> Map.ofList

    /// Routes one task: a ladder (one catalog model per tier) or a typed refusal.
    /// Per tier, every eligible model gets one Thompson draw p and the lowest expectedCost / p wins.
    let route
        (policy: RoutingPolicy)
        (rng: Rng)
        (stats: Stats)
        (budget: BudgetState)
        (task: TaskSpec)
        (catalog: ModelEntry list)
        : RouteOutcome =
        match budget with
        | BudgetState.Blocked -> BudgetExceeded
        | BudgetState.Downgrade when policy.MinTier task.Risk = Frontier ->
            NeedsDecision $"budget is in downgrade but risk {task.Risk} needs a Frontier model"
        | _ ->
            let checks = catalog |> List.map (fun m -> m, eligible policy budget task m)
            let eligibleModels = checks |> List.choose (function m, Ok() -> Some m | _ -> None)
            if List.isEmpty eligibleModels then
                checks
                |> List.map (fun (m, r) ->
                    { Model = m.Id; Reasons = (match r with Error rs -> rs | Ok() -> []) })
                |> NoEligibleModel
            else
                let score m =
                    let p = max (successDraw rng stats task m) 1e-9
                    float (expectedCost policy stats task m) / p
                let picks =
                    eligibleModels
                    |> List.map (fun m -> m, score m)
                    |> List.groupBy (fun (m, _) -> m.Tier)
                    |> List.map (fun (tier, scored) -> tier, scored |> List.minBy snd |> fst)
                    |> Map.ofList
                let warnings = if budget = BudgetState.Soft then [ "budget soft limit reached" ] else []
                Routed(fill picks, warnings)

    /// Builds the router's view of one planned task for one role.
    /// Risk is the highest Risk among the task's elements (Low if none is found). Size is a heuristic from the
    /// number of declared writes plus test writes, because real size is unknown until the work is done; phase 3
    /// replaces it with learned data. Repo is None until split repos arrive. ContextTokens is the character count
    /// of the brief and context bundle over four, which the caller measures. Only the Reviewer excludes families:
    /// the ones the implementer's ladder uses, so the review stays independent.
    let taskSpec
        (config: TaskSpecConfig)
        (handover: Handover)
        (task: PlannedTask)
        (role: Role)
        (contextChars: int)
        (implementerFamilies: Set<string>)
        : TaskSpec =
        let riskRank r =
            match r with
            | Low -> 0
            | Medium -> 1
            | High -> 2
        let risk =
            handover.Elements
            |> List.filter (fun e -> List.contains e.Id task.Elements)
            |> List.map (fun e -> e.Risk)
            |> List.fold (fun acc r -> if riskRank r > riskRank acc then r else acc) Low
        let writes = task.Writes.Length + task.TestWrites.Length
        let size =
            if writes <= config.SmallMaxWrites then S
            elif writes <= config.MediumMaxWrites then M
            else L
        let needs =
            match role with
            | Role.TestAuthor | Role.Implementer | Role.Qa -> Set.ofList [ ToolUse ]
            | Role.Planner | Role.TaskPlanner -> Set.ofList [ StructuredOutput ]
            | Role.Reviewer -> Set.empty
        let estIn, estOut = config.Estimates role
        { TaskId = task.Id.Value
          Role = role
          Repo = None
          Needs = needs
          Risk = risk
          DataClass = config.DataClass
          Size = size
          ContextTokens = contextChars / 4
          EstTokensIn = estIn
          EstTokensOut = estOut
          ExcludeFamilies = (if role = Role.Reviewer then implementerFamilies else Set.empty) }
