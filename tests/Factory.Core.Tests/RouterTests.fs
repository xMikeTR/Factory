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

let private elem id risk : ElementSpec =
    { Id = ElementId id; Name = id; CodeAreas = []; Risk = risk; InScope = true; Repo = None }

let private specHandover: Handover =
    { Id = "h"
      Digest = ""
      Elements = [ elem "a" Low; elem "b" High ]
      Decisions = []
      ProtectedPaths = []
      EventVocabulary = []
      Relationships = []
      AcceptanceSpecs = []
      WorkItem = None
      DesignTokens = None
      DesignVersion = None
      AcceptanceCriteria = []
      LoopSpecs = []
      References = []
      Contracts = []
      Uncovered = []
      ApiContracts = []
      RequirementRisk = None
      RequirementDataClass = None }

let private specConfig: TaskSpecConfig =
    { SmallMaxWrites = 4
      MediumMaxWrites = 10
      DataClass = Internal
      Estimates = fun _ -> 111, 222 }

let private specTask (elements: string list) (writes: int) (testWrites: int) : PlannedTask =
    let pats n pre = [ for i in 1..n -> PathPattern.createOrFail $"{pre}{i}/**" ]
    { Id = TaskId "T-1"
      Title = ""
      Instructions = ""
      Elements = elements |> List.map ElementId
      Writes = pats writes "src/"
      TestWrites = pats testWrites "tests/"
      DependsOn = []
      Covers = []
      Verify = [] }

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
              check (abs (mean - expected) <= 0.02) $"beta {a} {b} mean {mean} within 0.02 of {expected}"
      "router: a chosen model never lacks a needed capability", fun () ->
          let catalog =
              [ model "a" "claude" Standard [ ToolUse ] 200000 allData
                model "b" "claude" Standard [ ToolUse; Vision ] 200000 allData
                model "c" "gemini" Frontier [ Vision ] 200000 allData ]
          let t = { task with Needs = Set.ofList [ Vision ] }
          for seed in 1..100 do
              match Router.route policy (System.Random(seed).NextDouble) Map.empty BudgetState.Ok t catalog with
              | Routed(ladder, _) ->
                  for m in Map.values ladder do check (Set.contains Vision m.Caps) $"seed {seed}: {m.Id} lacks Vision"
              | other -> failwith $"unexpected %A{other}"
      "router: a Regulated task never routes to a model not approved for Regulated", fun () ->
          let catalog =
              [ model "a" "claude" Standard [] 200000 [ Public; Internal ]
                model "b" "claude" Standard [] 200000 allData
                model "c" "gemini" Frontier [] 200000 [ Public ] ]
          for seed in 1..100 do
              match Router.route policy (System.Random(seed).NextDouble) Map.empty BudgetState.Ok { task with DataClass = Regulated } catalog with
              | Routed(ladder, _) ->
                  for m in Map.values ladder do check (Set.contains Regulated m.ApprovedFor) $"seed {seed}: {m.Id} not approved"
              | other -> failwith $"unexpected %A{other}"
      "router: a model whose window does not exceed the task context is never chosen", fun () ->
          let catalog =
              [ model "small-window" "claude" Standard [] 10000 allData
                model "big-window" "claude" Standard [] 50000 allData ]
          for seed in 1..100 do
              match Router.route policy (System.Random(seed).NextDouble) Map.empty BudgetState.Ok task catalog with
              | Routed(ladder, _) ->
                  for m in Map.values ladder do equal "big-window" m.Id $"seed {seed}"
              | other -> failwith $"unexpected %A{other}"
      "router: an excluded family is never chosen", fun () ->
          let catalog =
              [ model "a" "claude" Standard [] 200000 allData
                model "b" "gemini" Standard [] 200000 allData ]
          for seed in 1..100 do
              match Router.route policy (System.Random(seed).NextDouble) Map.empty BudgetState.Ok { task with ExcludeFamilies = Set.ofList [ "claude" ] } catalog with
              | Routed(ladder, _) ->
                  for m in Map.values ladder do equal "gemini" m.Family $"seed {seed}"
              | other -> failwith $"unexpected %A{other}"
      "router: a blocked budget refuses with BudgetExceeded", fun () ->
          let catalog = [ model "a" "claude" Standard [] 200000 allData ]
          equal BudgetExceeded (Router.route policy (System.Random(1).NextDouble) Map.empty BudgetState.Blocked task catalog) "blocked"
      "router: under Downgrade Frontier resolves to a Standard model", fun () ->
          let catalog =
              [ model "std" "claude" Standard [] 200000 allData
                model "fr" "claude" Frontier [] 200000 allData ]
          match Router.route policy (System.Random(1).NextDouble) Map.empty BudgetState.Downgrade task catalog with
          | Routed(ladder, _) ->
              equal "std" ladder.[Frontier].Id "frontier capped"
              equal "std" ladder.[Standard].Id "standard"
          | other -> failwith $"unexpected %A{other}"
      "router: Downgrade with a risk that needs Frontier asks for a decision", fun () ->
          let strict = { policy with MinTier = (fun _ -> Frontier) }
          let catalog = [ model "fr" "claude" Frontier [] 200000 allData ]
          match Router.route strict (System.Random(1).NextDouble) Map.empty BudgetState.Downgrade { task with Risk = High } catalog with
          | NeedsDecision reason -> check (reason.Contains "High") "reason names the risk"
          | other -> failwith $"unexpected %A{other}"
      "router: a soft budget routes with a warning", fun () ->
          let catalog = [ model "a" "claude" Standard [] 200000 allData ]
          match Router.route policy (System.Random(1).NextDouble) Map.empty BudgetState.Soft task catalog with
          | Routed(_, warnings) -> equal [ "budget soft limit reached" ] warnings "warning"
          | other -> failwith $"unexpected %A{other}"
      "router: an ok budget routes without warnings", fun () ->
          let catalog = [ model "a" "claude" Standard [] 200000 allData ]
          match Router.route policy (System.Random(1).NextDouble) Map.empty BudgetState.Ok task catalog with
          | Routed(_, warnings) -> equal [] warnings "no warnings"
          | other -> failwith $"unexpected %A{other}"
      "router: NoEligibleModel lists every model with every reason", fun () ->
          let t = { task with Needs = Set.ofList [ Vision ]; DataClass = Regulated; Risk = High }
          let catalog =
              [ model "a" "claude" Small [] 100 [ Public ]
                model "b" "gemini" Small [ ToolUse ] 100 [ Public ] ]
          match Router.route policy (System.Random(1).NextDouble) Map.empty BudgetState.Ok t catalog with
          | NoEligibleModel rows ->
              equal [ "a"; "b" ] (rows |> List.map _.Model) "every model listed"
              for r in rows do equal 4 (List.length r.Reasons) $"{r.Model}: capability, window, data class, tier"
          | other -> failwith $"unexpected %A{other}"
      "router: an empty catalog has no eligible model", fun () ->
          equal (NoEligibleModel []) (Router.route policy (System.Random(1).NextDouble) Map.empty BudgetState.Ok task []) "empty"
      "router: at risk Medium the Small tier resolves to the Standard pick", fun () ->
          let catalog =
              [ model "sm" "claude" Small [] 200000 allData
                model "std" "claude" Standard [] 200000 allData
                model "fr" "claude" Frontier [] 200000 allData ]
          match Router.route policy (System.Random(1).NextDouble) Map.empty BudgetState.Ok { task with Risk = Medium } catalog with
          | Routed(ladder, _) ->
              equal "std" ladder.[Small].Id "small -> standard"
              equal "fr" ladder.[Frontier].Id "frontier"
          | other -> failwith $"unexpected %A{other}"
      "router: with no eligible Frontier model Frontier resolves to the Standard pick", fun () ->
          let catalog =
              [ model "sm" "claude" Small [] 200000 allData
                model "std" "claude" Standard [] 200000 allData ]
          match Router.route policy (System.Random(1).NextDouble) Map.empty BudgetState.Ok task catalog with
          | Routed(ladder, _) ->
              equal "sm" ladder.[Small].Id "small"
              equal "std" ladder.[Frontier].Id "frontier -> standard"
          | other -> failwith $"unexpected %A{other}"
      "router: a gap between routed tiers resolves upward", fun () ->
          let catalog =
              [ model "sm" "claude" Small [] 200000 allData
                model "fr" "claude" Frontier [] 200000 allData ]
          match Router.route policy (System.Random(1).NextDouble) Map.empty BudgetState.Ok task catalog with
          | Routed(ladder, _) ->
              equal "sm" ladder.[Small].Id "small"
              equal "fr" ladder.[Standard].Id "standard -> frontier"
              equal "fr" ladder.[Frontier].Id "frontier"
          | other -> failwith $"unexpected %A{other}"
      "router: the ladder always has all three tiers", fun () ->
          let catalog = [ model "std" "claude" Standard [] 200000 allData ]
          match Router.route policy (System.Random(1).NextDouble) Map.empty BudgetState.Ok task catalog with
          | Routed(ladder, _) -> equal [ Small; Standard; Frontier ] (ladder |> Map.keys |> Seq.sortBy Router.tierRank |> List.ofSeq) "tiers"
          | other -> failwith $"unexpected %A{other}"
      "router: the same seed and inputs give the same outcome", fun () ->
          let catalog =
              [ model "a" "claude" Standard [] 200000 allData
                model "b" "gemini" Standard [] 200000 allData
                model "c" "claude" Frontier [] 200000 allData ]
          let run () = Router.route policy (System.Random(7).NextDouble) Map.empty BudgetState.Ok task catalog
          equal (run ()) (run ()) "same outcome"
      "router: expected cost prices the task estimates until there are enough samples, then the means", fun () ->
          let m = model "a" "claude" Standard [] 200000 allData // 3 in, 15 out per MTok
          let key = { Model = "a"; Role = Role.Implementer; Repo = None; Size = M }
          let counts samples = { Successes = 0.0; Failures = 0.0; MeanTokensIn = 1000000.0; MeanTokensOut = 1000000.0; Samples = samples }
          // estimates: 20000 in, 4000 out = 0.06 + 0.06
          equal 0.12m (Router.expectedCost policy Map.empty task m) "no stats"
          equal 0.12m (Router.expectedCost policy (Map [ key, counts 4 ]) task m) "below the threshold"
          equal 18m (Router.expectedCost policy (Map [ key, counts 5 ]) task m) "at the threshold: 3 + 15"
      "router: a success draw with empty stats uses only the prior", fun () ->
          let m = { model "a" "claude" Standard [] 200000 allData with Prior = (45.0, 5.0) }
          let draws = [ let rng = System.Random(3).NextDouble in for _ in 1..2000 -> Router.successDraw rng Map.empty task m ]
          // Beta(46, 6) has mean 46/52
          check (abs (List.average draws - 46.0 / 52.0) < 0.02) "mean follows the prior"
      "router: a model with a strong prior beats an equally priced model with the default prior", fun () ->
          let strong = { model "strong" "claude" Standard [] 200000 allData with Prior = (45.0, 5.0) }
          let blank = model "blank" "gemini" Standard [] 200000 allData
          let wins =
              [ for seed in 1..200 ->
                    match Router.route policy (System.Random(seed).NextDouble) Map.empty BudgetState.Ok task [ blank; strong ] with
                    | Routed(ladder, _) -> ladder.[Standard].Id
                    | other -> failwith $"unexpected %A{other}" ]
              |> List.filter ((=) "strong")
              |> List.length
          check (wins >= 160) $"strong prior won {wins} of 200"
      "router: recording an attempt counts the outcome and updates the running token means", fun () ->
          let key = { Model = "a"; Role = Role.Implementer; Repo = None; Size = M }
          let stats = Map.empty |> Stats.record key true 1000 100 |> Stats.record key false 3000 300
          let c = stats.[key]
          equal 1.0 c.Successes "successes"
          equal 1.0 c.Failures "failures"
          equal 2 c.Samples "samples"
          equal 2000.0 c.MeanTokensIn "mean in"
          equal 200.0 c.MeanTokensOut "mean out"
      "router: recording keeps other keys untouched", fun () ->
          let a = { Model = "a"; Role = Role.Implementer; Repo = None; Size = M }
          let b = { a with Model = "b" }
          let stats = Map.empty |> Stats.record a true 10 10 |> Stats.record b false 20 20
          equal 1.0 stats.[a].Successes "a"
          equal 0.0 stats.[a].Failures "a failures"
          equal 1.0 stats.[b].Failures "b"
      "router: decay halves the counts and keeps the means and samples", fun () ->
          let key = { Model = "a"; Role = Role.Implementer; Repo = None; Size = M }
          let stats =
              Map.empty
              |> Stats.record key true 1000 100
              |> Stats.record key true 1000 100
              |> Stats.record key false 1000 100
              |> Stats.record key false 1000 100
              |> Stats.decay 0.5
          let c = stats.[key]
          equal 1.0 c.Successes "successes"
          equal 1.0 c.Failures "failures"
          equal 4 c.Samples "samples unchanged"
          equal 1000.0 c.MeanTokensIn "mean in unchanged"
          equal 100.0 c.MeanTokensOut "mean out unchanged"
      "router: learning converges on the model with the lower cost per success", fun () ->
          // cheap: 3/15 per MTok at a 0.9 pass rate; pricey: 6/30 at 0.95. Cost per success favours cheap.
          let cheap = model "cheap" "claude" Standard [] 200000 allData
          let pricey = { model "pricey" "gemini" Standard [] 200000 allData with Price = { InPerMTok = 6m; OutPerMTok = 30m } }
          let truth = Map [ "cheap", 0.9; "pricey", 0.95 ]
          let routeRng = System.Random(11).NextDouble
          let outcomeRng = System.Random(12)
          let mutable stats: Stats = Map.empty
          let picks =
              [ for _ in 1..300 ->
                    match Router.route policy routeRng stats BudgetState.Ok task [ cheap; pricey ] with
                    | Routed(ladder, _) ->
                        let pick = ladder.[Standard]
                        let passed = outcomeRng.NextDouble() < truth.[pick.Id]
                        let key = { Model = pick.Id; Role = task.Role; Repo = task.Repo; Size = task.Size }
                        stats <- Stats.record key passed task.EstTokensIn task.EstTokensOut stats
                        pick.Id
                    | other -> failwith $"unexpected %A{other}" ]
          let cheapInLast100 = picks |> List.skip 200 |> List.filter ((=) "cheap") |> List.length
          check (cheapInLast100 >= 80) $"cheap picked {cheapInLast100} of the last 100"
      "router: taskSpec takes the highest risk among the task's elements", fun () ->
          let t = specTask [ "a"; "b" ] 1 0
          equal High (Router.taskSpec specConfig specHandover t Role.Implementer 0 Set.empty).Risk "high wins"
          let low = specTask [ "a" ] 1 0
          equal Low (Router.taskSpec specConfig specHandover low Role.Implementer 0 Set.empty).Risk "low"
      "router: taskSpec prefers the requirement's risk and data class over the element max and config", fun () ->
          let t = specTask [ "a" ] 1 0
          let h = { specHandover with RequirementRisk = Some Low; RequirementDataClass = Some "regulated" }
          let s = Router.taskSpec specConfig h t Role.Implementer 0 Set.empty
          equal Regulated s.DataClass "requirement data class"
          equal Low s.Risk "requirement risk applied"
          equal High (Router.taskSpec specConfig { h with RequirementRisk = Some High } t Role.Implementer 0 Set.empty).Risk "requirement high"
          equal DataClass.Internal (Router.taskSpec { specConfig with DataClass = DataClass.Internal } specHandover t Role.Implementer 0 Set.empty).DataClass "config when absent"
      "router: parseHandover reads requirementRisk (critical is high) and ignores unknown values", fun () ->
          let json = System.IO.File.ReadAllText(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "fares-v3", "handover.json"))
          let withKeys r d = json.Replace(",\n  \"requirementRisk\": \"high\",\n  \"requirementDataClass\": \"internal\"", "").TrimEnd().TrimEnd('}') + sprintf ",\"requirementRisk\":\"%s\",\"requirementDataClass\":\"%s\"}" r d
          let get j = match Json.parseHandover j with Ok h -> h | Error es -> failwithf "%A" es
          equal (Some High) (get (withKeys "critical" "regulated")).RequirementRisk "critical"
          equal (Some "regulated") (get (withKeys "low" "regulated")).RequirementDataClass "dc"
          equal None (get (withKeys "bogus" "bogus")).RequirementRisk "unknown risk"
          let plain = json.Replace(",\n  \"requirementRisk\": \"high\",\n  \"requirementDataClass\": \"internal\"", "")
          equal None (get plain).RequirementDataClass "absent"
          let back = get (Json.writeHandover (get (withKeys "high" "internal")))
          equal (Some High, Some "internal") (back.RequirementRisk, back.RequirementDataClass) "round trip"
      "router: taskSpec carries the repo of the task's elements, None when the design declares none", fun () ->
          let h = { specHandover with Elements = [ { elem "a" Low with Repo = Some "Api" }; { elem "b" High with Repo = Some "Ui" } ] }
          equal (Some "Api") (Router.taskSpec specConfig h (specTask [ "a" ] 1 0) Role.Implementer 0 Set.empty).Repo "api"
          equal (Some "Ui") (Router.taskSpec specConfig h (specTask [ "b" ] 1 0) Role.Implementer 0 Set.empty).Repo "ui"
          equal None (Router.taskSpec specConfig specHandover (specTask [ "a" ] 1 0) Role.Implementer 0 Set.empty).Repo "no repos declared"
      "router: taskSpec risk is Low when the task names no known element", fun () ->
          let t = specTask [ "zzz" ] 1 0
          equal Low (Router.taskSpec specConfig specHandover t Role.Implementer 0 Set.empty).Risk "unknown element"
      "router: taskSpec needs follow the role", fun () ->
          let t = specTask [ "a" ] 1 0
          let needs r = (Router.taskSpec specConfig specHandover t r 0 Set.empty).Needs
          equal (Set.ofList [ ToolUse ]) (needs Role.TestAuthor) "test author"
          equal (Set.ofList [ ToolUse ]) (needs Role.Implementer) "implementer"
          equal (Set.ofList [ ToolUse ]) (needs Role.Qa) "qa"
          equal (Set.ofList [ StructuredOutput ]) (needs Role.Planner) "planner"
          equal (Set.ofList [ StructuredOutput ]) (needs Role.TaskPlanner) "task planner"
          equal Set.empty (needs Role.Reviewer) "reviewer"
      "router: taskSpec size buckets count writes plus test writes", fun () ->
          let size w tw = (Router.taskSpec specConfig specHandover (specTask [ "a" ] w tw) Role.Implementer 0 Set.empty).Size
          equal S (size 1 1) "2 writes is S"
          equal S (size 2 2) "4 writes is S at the boundary"
          equal M (size 3 2) "5 is M"
          equal M (size 5 5) "10 is M at the boundary"
          equal L (size 6 5) "11 is L"
      "router: taskSpec context tokens are characters over four, estimates come from the role", fun () ->
          let s = Router.taskSpec specConfig specHandover (specTask [ "a" ] 1 0) Role.Planner 4001 Set.empty
          equal 1000 s.ContextTokens "4001 chars / 4"
          equal 111 s.EstTokensIn "planner in"
          equal 222 s.EstTokensOut "planner out"
          equal None s.Repo "repo is None until phase 5"
          equal "T-1" s.TaskId "task id"
      "router: taskSpec excludes families only for the reviewer", fun () ->
          let fams = Set.ofList [ "claude" ]
          let ex r = (Router.taskSpec specConfig specHandover (specTask [ "a" ] 1 0) r 0 fams).ExcludeFamilies
          equal fams (ex Role.Reviewer) "reviewer"
          equal Set.empty (ex Role.Implementer) "implementer"
          equal Set.empty (ex Role.Qa) "qa" ]

