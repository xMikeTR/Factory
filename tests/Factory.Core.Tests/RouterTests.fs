module Factory.Core.Tests.RouterTests

open Factory.Core
open Factory.Core.Tests.Support

let private model id family tier caps window approved : ModelEntry =
    { Id = id
      Family = family
      Protocol = AnthropicVertex
      ModelName = id
      Tier = tier
      Price = { InPerMTok = 3m; OutPerMTok = 15m }
      Caps = Set.ofList caps
      ContextWindow = window
      ApprovedFor = Set.ofList approved
      Prior = (1.0, 1.0) }

let private allData = [ Public; Internal; Regulated ]

let private task: TaskSpec =
    { TaskId = "T-1"
      Role = Role.Implementer
      Repo = None
      Needs = Set.empty
      Risk = Low
      DataClass = Internal
      Size = M
      ContextTokens = 10000
      EstTokensIn = 20000
      EstTokensOut = 4000
      ExcludeFamilies = Set.empty }

let private policy = Router.defaultPolicy

let private reasons r =
    match r with
    | Ok () -> []
    | Error rs -> rs

let tests: (string * (unit -> unit)) list =
    [ "router: default policy maps Low to Small, Medium and High to Standard", fun () ->
          equal Small (policy.MinTier Low) "low"
          equal Standard (policy.MinTier Medium) "medium"
          equal Standard (policy.MinTier High) "high"
          equal 5 policy.MinSamplesForMeans "min samples"
      "router: tier order is Small < Standard < Frontier", fun () ->
          check (Router.tierRank Small < Router.tierRank Standard) "small<standard"
          check (Router.tierRank Standard < Router.tierRank Frontier) "standard<frontier"
      "router: a model that meets every constraint is eligible", fun () ->
          let m = model "a" "claude" Standard [ ToolUse; Vision ] 200000 allData
          equal [] (reasons (Router.eligible policy BudgetState.Ok { task with Needs = Set.ofList [ ToolUse ] } m)) "eligible"
      "router: a missing capability makes a model ineligible", fun () ->
          let m = model "a" "claude" Standard [ ToolUse ] 200000 allData
          let r = Router.eligible policy BudgetState.Ok { task with Needs = Set.ofList [ Vision ] } m
          equal 1 (List.length (reasons r)) "one reason"
      "router: a context window not larger than the task context is ineligible", fun () ->
          let m = model "a" "claude" Standard [] 10000 allData
          check (Result.isError (Router.eligible policy BudgetState.Ok task m)) "window equal to context is ineligible"
          let m2 = model "a" "claude" Standard [] 10001 allData
          check (Result.isOk (Router.eligible policy BudgetState.Ok task m2)) "window above context is eligible"
      "router: a model not approved for the data class is ineligible", fun () ->
          let m = model "a" "claude" Standard [] 200000 [ Public; Internal ]
          check (Result.isError (Router.eligible policy BudgetState.Ok { task with DataClass = Regulated } m)) "regulated"
      "router: an excluded family is ineligible", fun () ->
          let m = model "a" "claude" Standard [] 200000 allData
          check (Result.isError (Router.eligible policy BudgetState.Ok { task with ExcludeFamilies = Set.ofList [ "claude" ] } m)) "excluded"
      "router: a tier below the risk minimum is ineligible", fun () ->
          let m = model "a" "claude" Small [] 200000 allData
          check (Result.isOk (Router.eligible policy BudgetState.Ok { task with Risk = Low } m)) "small ok for low"
          check (Result.isError (Router.eligible policy BudgetState.Ok { task with Risk = Medium } m)) "small not ok for medium"
      "router: under Downgrade a Frontier model is ineligible", fun () ->
          let m = model "a" "claude" Frontier [] 200000 allData
          check (Result.isOk (Router.eligible policy BudgetState.Soft task m)) "frontier ok under soft"
          check (Result.isError (Router.eligible policy BudgetState.Downgrade task m)) "frontier not ok under downgrade"
      "router: eligibility collects every failing reason, not just the first", fun () ->
          let m = model "a" "claude" Small [ ToolUse ] 100 [ Public ]
          let t =
              { task with
                  Needs = Set.ofList [ Vision ]
                  Risk = High
                  DataClass = Regulated
                  ExcludeFamilies = Set.ofList [ "claude" ] }
          equal 5 (List.length (reasons (Router.eligible policy BudgetState.Ok t m))) "capability, window, data class, family, tier"
          let f = model "b" "claude" Frontier [ ToolUse ] 100 [ Public ]
          equal 5 (List.length (reasons (Router.eligible policy BudgetState.Downgrade t f))) "capability, window, data class, family, downgrade"
      "router: beta draws stay in (0,1) and average a/(a+b), for shapes above and below one", fun () ->
          for (a, b) in [ (2.0, 8.0); (0.5, 0.5); (0.3, 2.0) ] do
              let rng = System.Random(42).NextDouble
              let draws = [ for _ in 1..10000 -> Sampling.beta rng a b ]
              check (draws |> List.forall (fun x -> x > 0.0 && x < 1.0)) $"beta {a} {b} in (0,1)"
              let mean = List.average draws
              let expected = a / (a + b)
              check (abs (mean - expected) <= 0.02) $"beta {a} {b} mean {mean} within 0.02 of {expected}" ]
