module Factory.Core.Tests.SplitRepoTests

open Factory.Core
open Factory.Core.Tests.Support

// The example handover re-read as a two-repo design: Quotes DB and Pricing API are the Api repo, Referral Worker stands in for the Ui repo.
let private examples = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "pricing-v3")
let private baseHandover =
    match Json.parseHandover (System.IO.File.ReadAllText(System.IO.Path.Combine(examples, "handover.json"))) with
    | Ok h -> h
    | Error es -> failwithf "%A" es
let private basePlan =
    match Json.parsePlan (System.IO.File.ReadAllText(System.IO.Path.Combine(examples, "plan.json"))) with
    | Ok p -> p
    | Error es -> failwithf "%A" es

let private repoFor (e: ElementSpec) = if e.Name = "Referral Worker" then Some "Ui" else Some "Api"
let private split contracts =
    { baseHandover with
        Elements = baseHandover.Elements |> List.map (fun e -> { e with Repo = repoFor e })
        ApiContracts = contracts }
// The UI task no longer waits on the API task by hand: ordering is the dispatcher's job.
let private plan =
    { basePlan with
        Tasks = basePlan.Tasks |> List.map (fun t -> if t.Id = TaskId "T-003" then { t with DependsOn = [] } else t) }

let private validated contracts = match PlanValidator.validate (split contracts) plan with Ok v -> v | Error es -> failwithf "%A" es
let private errorsWith known h pl = match PlanValidator.validateWithRepos known h pl with Ok _ -> [] | Error es -> es
let private waveOf (d: DispatchPlan) (id: string) =
    d.Waves |> List.find (fun w -> w.Teams |> List.exists (fun t -> t.Tasks |> List.exists (fun x -> x.Id = TaskId id))) |> fun w -> w.Index

let tests: (string * (unit -> unit)) list =
    [ "split repos: the handover json round-trips repo and apiContracts", fun () ->
          let h = split [ "contracts/openapi/pricing.yaml" ]
          match Json.parseHandover (Json.writeHandover h) with
          | Error es -> failwithf "%A" es
          | Ok back ->
              equal (h.Elements |> List.map (fun e -> e.Repo)) (back.Elements |> List.map (fun e -> e.Repo)) "repos"
              equal [ "contracts/openapi/pricing.yaml" ] back.ApiContracts "contracts"
      "split repos: an older handover has no repo and no api contracts", fun () ->
          check (baseHandover.Elements |> List.forall (fun e -> e.Repo = None)) "repo"
          equal [] baseHandover.ApiContracts "contracts"
      "split repos: a task's repo is the single repo of its elements", fun () ->
          let v = validated []
          equal (Some "Api") (v.RepoOf(TaskId "T-001")) "api task"
          equal (Some "Ui") (v.RepoOf(TaskId "T-003")) "ui task"
      "split repos: a single-repo design has no task repo", fun () ->
          let v = match PlanValidator.validate baseHandover plan with Ok v -> v | Error es -> failwithf "%A" es
          equal None (v.RepoOf(TaskId "T-001")) "none"
      "validator: a task whose elements span repos is MixedRepoTask", fun () ->
          let mixed = { plan with Tasks = plan.Tasks |> List.map (fun t -> if t.Id = TaskId "T-002" then { t with Elements = t.Elements @ [ ElementId "Pricing Platform/Referral Worker" ]; Writes = t.Writes @ [ PathPattern.createOrFail "src/ReferralWorker/**" ] } else t) }
          let es = errorsWith [ "Api"; "Ui" ] (split []) mixed
          check (es |> List.exists (function MixedRepoTask(t, rs) -> t = TaskId "T-002" && rs = [ "Api"; "Ui" ] | _ -> false)) $"%A{es}"
      "validator: an element repo that is not a known repo is UnknownRepo", fun () ->
          let es = errorsWith [ "Api" ] (split []) plan
          check (es |> List.exists (function UnknownRepo(t, "Ui") -> t = TaskId "T-003" | _ -> false)) $"%A{es}"
          equal [] (errorsWith [ "Api"; "Ui" ] (split []) plan) "all known"
          equal [] (errorsWith [] (split []) plan) "no repo map given: not checked"
      "validator: a write into the other repo's code area is rejected", fun () ->
          let stray = { plan with Tasks = plan.Tasks |> List.map (fun t -> if t.Id = TaskId "T-003" then { t with Writes = t.Writes @ [ PathPattern.createOrFail "src/PricingApi/Hack.cs" ] } else t) }
          let es = errorsWith [ "Api"; "Ui" ] (split []) stray
          check (es |> List.exists (function WriteOutsideElement(t, _) -> t = TaskId "T-003" | _ -> false)) $"%A{es}"
      "dispatcher: without a pinned contract every Ui task waits for every Api task", fun () ->
          let d = Dispatcher.dispatch (validated [])
          check (waveOf d "T-003" > waveOf d "T-001") "after T-001"
          check (waveOf d "T-003" > waveOf d "T-002") "after T-002"
          check (waveOf d "T-003" > waveOf d "T-004") "after T-004"
      "dispatcher: with a pinned contract Ui and Api tasks run in parallel", fun () ->
          let d = Dispatcher.dispatch (validated [ "contracts/openapi/pricing.yaml" ])
          equal 0 (waveOf d "T-003") "ui in wave 0"
          equal 0 (waveOf d "T-001") "api in wave 0"
      "dispatcher: a team never spans repos, even when write areas overlap across repos", fun () ->
          // Both repos use src/** style areas: the same glob in two repos is two different directories.
          let h = { split [] with Elements = split [] |> fun s -> s.Elements |> List.map (fun e -> { e with CodeAreas = [ PathPattern.createOrFail "src/**"; PathPattern.createOrFail "tests/**"; PathPattern.createOrFail "db/**" ] }) }
          let pl =
              { plan with
                  Tasks =
                    plan.Tasks
                    |> List.map (fun t -> { t with Writes = [ PathPattern.createOrFail "src/shared/**" ] }) }
          match PlanValidator.validate h pl with
          | Error es -> failwithf "%A" es
          | Ok v ->
              let repoOfTeam (t: Team) = t.Tasks |> List.choose (fun x -> v.RepoOf x.Id) |> List.distinct
              let d = Dispatcher.dispatch v
              check (d.Waves |> List.forall (fun w -> w.Teams |> List.forall (fun t -> (repoOfTeam t).Length <= 1))) "single repo per team" ]
