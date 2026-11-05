module Factory.Core.Tests.LearningTests

open System
open Factory.Core
open Factory.Core.Tests.Support

let private t0 = DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)
let private at (min: int) = t0.AddMinutes(float min)

let private route task role size = LearnEvent.Routed { Task = task; Role = role; Repo = None; Size = size }
let private turn task role model tin tout min =
    LearnEvent.Turn { Task = task; Role = role; Model = model; TokensIn = tin; TokensOut = tout; Ts = at min }
let private verify task passed min = LearnEvent.Verified { Task = task; Passed = passed; Ts = at min }
let private review task model approved min = LearnEvent.Reviewed { Task = task; Model = model; Approved = approved; Ts = at min }

let private known = fun (m: string) -> m <> "ghost"
let private attribute learnReviewer events = Learning.attribute known learnReviewer events
let private key model role size : StatsKey = { Model = model; Role = role; Repo = None; Size = size }

let private canaryLadder () : Ladder =
    let mk id tier : ModelEntry =
        { Id = id; Family = "claude"; Protocol = AnthropicVertex; ModelName = id; Tier = tier
          Price = { InPerMTok = 1m; OutPerMTok = 5m }; Caps = Set.empty; ContextWindow = 200000
          ApprovedFor = Set.ofList [ Public; Internal; Regulated ]; Prior = (1.0, 1.0) }
    Map [ Small, mk "small" Small; Standard, mk "std" Standard; Frontier, mk "top" Frontier ]

let tests : (string * (unit -> unit)) list =
    [ "learning: a verify pass credits the model of the act turn before it, with that model's tokens",
      fun () ->
          let r = attribute false [ route "T" Role.Implementer M; turn "T" Role.Implementer "small" 100 20 1; turn "T" Role.Implementer "small" 50 10 2; verify "T" true 3 ]
          equal [ (key "small" Role.Implementer M, true, 150, 30, at 3) ] r "one success, tokens summed"
      "learning: fail, fail, pass: each verify credits the act turn just before it (escalation credits frontier with the pass)",
      fun () ->
          let r =
              attribute false
                  [ route "T" Role.Implementer L
                    turn "T" Role.Implementer "small" 10 1 1; verify "T" false 2
                    turn "T" Role.Implementer "std" 20 2 3; verify "T" false 4
                    turn "T" Role.Implementer "top" 30 3 5; verify "T" true 6 ]
          equal [ (key "small" Role.Implementer L, false, 10, 1, at 2)
                  (key "std" Role.Implementer L, false, 20, 2, at 4)
                  (key "top" Role.Implementer L, true, 30, 3, at 6) ] r "per-verify credit"
      "learning: the test author's turn is credited under its own role key",
      fun () ->
          let r = attribute false [ route "T" Role.TestAuthor S; turn "T" Role.TestAuthor "small" 5 5 1; verify "T" true 2 ]
          equal [ (key "small" Role.TestAuthor S, true, 5, 5, at 2) ] r "test-author key"
      "learning: a verify with no act turn before it, or no route row, gives no evidence",
      fun () ->
          equal [] (attribute false [ route "T" Role.Implementer M; verify "T" true 1 ]) "no act turn"
          equal [] (attribute false [ turn "T" Role.Implementer "small" 1 1 1; verify "T" true 2 ]) "no route row"
      "learning: an unknown model_id is ignored, the rest still counts",
      fun () ->
          let r = attribute false [ route "T" Role.Implementer M; turn "T" Role.Implementer "ghost" 1 1 1; verify "T" false 2; turn "T" Role.Implementer "std" 1 1 3; verify "T" true 4 ]
          equal [ (key "std" Role.Implementer M, true, 1, 1, at 4) ] r "ghost dropped"
      "learning: tasks do not mix",
      fun () ->
          let r =
              attribute false
                  [ route "A" Role.Implementer M; route "B" Role.Implementer M
                    turn "A" Role.Implementer "small" 1 1 1; turn "B" Role.Implementer "std" 2 2 2
                    verify "B" true 3; verify "A" false 4 ]
          equal [ (key "std" Role.Implementer M, true, 2, 2, at 3); (key "small" Role.Implementer M, false, 1, 1, at 4) ] r "per task"
      "learning: the reviewer verdict counts only when learnReviewer is on",
      fun () ->
          let events = [ route "T" Role.Reviewer M; review "T" "other" true 1 ]
          equal [] (attribute false events) "off"
          equal [ (key "other" Role.Reviewer M, true, 0, 0, at 1) ] (attribute true events) "on"
          equal [ (key "other" Role.Reviewer M, false, 0, 0, at 1) ] (attribute true [ route "T" Role.Reviewer M; review "T" "other" false 1 ]) "rejection"
      "learning: decay weight halves every half-life",
      fun () ->
          check (abs (Learning.weight t0 30.0 t0 - 1.0) < 1e-9) "age 0"
          check (abs (Learning.weight (t0.AddDays 30.0) 30.0 t0 - 0.5) < 1e-9) "one half-life"
          check (abs (Learning.weight (t0.AddDays 60.0) 30.0 t0 - 0.25) < 1e-9) "two"
          equal 1.0 (Learning.weight t0 30.0 (t0.AddDays 5.0)) "future events are not boosted"
      "learning: fold weights old evidence and keeps token means",
      fun () ->
          let k = key "small" Role.Implementer M
          let events = [ (k, true, 100, 10, t0); (k, false, 300, 30, t0.AddDays -30.0) ]
          let s = Learning.fold t0 30.0 Map.empty events
          let c = s.[k]
          check (abs (c.Successes - 1.0) < 1e-9) "fresh success"
          check (abs (c.Failures - 0.5) < 1e-9) "30-day-old failure at half weight"
          equal 2 c.Samples "samples"
          check (abs (c.MeanTokensIn - 200.0) < 1e-9) "mean in"
      "learning: fold starts from the given stats (priors stay with the catalog, so the start is usually empty)",
      fun () ->
          let k = key "small" Role.Implementer M
          let start = Stats.record k true 10 1 Map.empty
          let s = Learning.fold t0 30.0 start [ (k, true, 30, 3, t0) ]
          equal 2.0 s.[k].Successes "adds to the start"
          equal 2 s.[k].Samples "samples"
      "learning: a ledger history where the small model passes 90% gives a ladder that picks it",
      fun () ->
          let mk id family tier : ModelEntry =
              { Id = id; Family = family; Protocol = AnthropicVertex; ModelName = id; Tier = tier
                Price = { InPerMTok = 1m; OutPerMTok = 5m }; Caps = Set.empty; ContextWindow = 200000
                ApprovedFor = Set.ofList [ Public; Internal; Regulated ]; Prior = (1.0, 1.0) }
          let good, bad, std, top = mk "good" "claude" Small, mk "bad" "gemini" Small, mk "std" "claude" Standard, mk "top" "claude" Frontier
          // 40 tasks each: good passes 36, bad passes 12; one act turn and one verify per task
          let history =
              [ yield route "T0" Role.Implementer M
                for i in 0..39 do
                    let task = $"G{i}"
                    yield route task Role.Implementer M
                    yield turn task Role.Implementer "good" 1000 100 (i * 2)
                    yield verify task (i < 36) (i * 2 + 1)
                for i in 0..39 do
                    let task = $"B{i}"
                    yield route task Role.Implementer M
                    yield turn task Role.Implementer "bad" 1000 100 (100 + i * 2)
                    yield verify task (i < 12) (100 + i * 2 + 1) ]
          let stats = Learning.fold (at 300) 30.0 Map.empty (attribute false history)
          let spec : TaskSpec =
              { TaskId = "T-new"; Role = Role.Implementer; Repo = None; Needs = Set.empty; Risk = Low; DataClass = Internal
                Size = M; ContextTokens = 1000; EstTokensIn = 1000; EstTokensOut = 100; ExcludeFamilies = Set.empty }
          let policy = { MinTier = (fun _ -> Small); MinSamplesForMeans = 5 }
          let picks =
              [ for seed in 1..50 ->
                    match Router.route policy (Random(seed).NextDouble) stats BudgetState.Ok spec [ good; bad; std; top ] with
                    | Routed(ladder, _) -> ladder.[Small].Id
                    | other -> failwith $"unexpected %A{other}" ]
          let goodPicks = picks |> List.filter ((=) "good") |> List.length
          check (goodPicks >= 45) $"good picked {goodPicks} of 50"
      "canary: with a share of 1 the standard entry becomes the frontier entry and the flag is set",
      fun () ->
          let ladder = canaryLadder ()
          let l, flag = Router.applyCanary 1.0 (fun () -> 0.99) ladder
          equal "top" l.[Standard].Id "standard replaced"
          equal "small" l.[Small].Id "small untouched"
          equal "top" l.[Frontier].Id "frontier untouched"
          check flag "canary flag"
      "canary: a share of 0 never fires, and a draw at or above the share does not fire",
      fun () ->
          let ladder = canaryLadder ()
          let l0, f0 = Router.applyCanary 0.0 (fun () -> 0.0) ladder
          equal ladder l0 "share 0"
          check (not f0) "no flag at 0"
          let l1, f1 = Router.applyCanary 0.05 (fun () -> 0.05) ladder
          equal ladder l1 "draw == share"
          check (not f1) "no flag"
      "canary: when standard already is the frontier model nothing changes and it is not a canary",
      fun () ->
          let ladder = canaryLadder () |> Map.add Standard (canaryLadder().[Frontier])
          let l, flag = Router.applyCanary 1.0 (fun () -> 0.0) ladder
          equal ladder l "unchanged"
          check (not flag) "no flag"
      "canary: over many seeded draws the rate is about the share",
      fun () ->
          let rng = Random(5).NextDouble
          let n = 4000
          let hits = [ for _ in 1..n -> snd (Router.applyCanary 0.05 rng (canaryLadder ())) ] |> List.filter id |> List.length
          let rate = float hits / float n
          check (rate > 0.03 && rate < 0.07) $"rate {rate}" ]

