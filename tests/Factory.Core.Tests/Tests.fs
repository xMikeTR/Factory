module Factory.Core.Tests.Tests

open System
open System.IO
open Factory.Core

// A tiny dependency-free runner so the core can be tested anywhere `dotnet` runs.
// Swap to xUnit/Expecto when the repo has a package feed; the test bodies port unchanged.
// These tests are the spec for every TODO(you) in Factory.Core. Work order: RESOURCES.md.
exception AssertFailed of string

let check cond msg = if not cond then raise (AssertFailed msg)
let equal expected actual msg = if expected <> actual then raise (AssertFailed $"{msg}: expected %A{expected}, got %A{actual}")

let p = PathPattern.createOrFail
let examples = Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "examples", "pricing-v3")
let handoverJson = File.ReadAllText(Path.Combine(examples, "handover.json"))
let planJson = File.ReadAllText(Path.Combine(examples, "plan.json"))

let handover =
    match Json.parseHandover handoverJson with
    | Ok h -> h
    | Error es -> failwithf "example handover: %A" es

let plan =
    match Json.parsePlan planJson with
    | Ok x -> x
    | Error es -> failwithf "example plan: %A" es

let task id = plan.Tasks |> List.find (fun t -> t.Id = TaskId id)
let withTask id (f: PlannedTask -> PlannedTask) = { plan with Tasks = plan.Tasks |> List.map (fun t -> if t.Id = TaskId id then f t else t) }
let errorsOf pl = match PlanValidator.validate handover pl with Ok _ -> [] | Error es -> es
let validated = lazy (match PlanValidator.validate handover plan with Ok v -> v | Error es -> failwithf "%A" es)
let limits = { MaxTurns = 3; MaxVerifyAttempts = 2 }
let outcome name code = { Name = name; ExitCode = code; DurationMs = 10L; OutputTail = "" }

let tests: (string * (unit -> unit)) list =
    [
      // ---------- paths ----------
      "paths: rejects unsafe patterns", fun () ->
          for bad in [ "../x"; "/etc/passwd"; "C:/x"; "src/{a,b}/**"; "src/[ab]"; "src/a**b"; "src//x"; "" ] do
              check (Result.isError (PathPattern.create bad)) $"should reject '{bad}'"
      "paths: normalises", fun () ->
          equal "src/Quotes" (p "./src\\Quotes/").Value "normalised"
      "paths: glob matching", fun () ->
          check (PathPattern.matches (p "src/**") "src/a/b.cs") "** deep"
          check (PathPattern.matches (p "src/*.cs") "src/a.cs") "* in segment"
          check (not (PathPattern.matches (p "src/*.cs") "src/a/b.cs")) "* does not cross /"
          check (PathPattern.matches (p "src/**/Data/*.cs") "src/x/y/Data/Q.cs") "** in middle"
      "paths: overlap", fun () ->
          check (PathPattern.mayOverlap (p "src/Quotes.Data/**") (p "src/Quotes.Data/Logging/**")) "nested globs"
          check (not (PathPattern.mayOverlap (p "src/PricingApi/**") (p "src/ReferralWorker/**"))) "sibling dirs"
          check (PathPattern.mayOverlap (p "src/a.cs") (p "src/*.cs")) "exact vs glob"
          check (not (PathPattern.mayOverlap (p "src/a.cs") (p "src/b.cs"))) "distinct files"
      "paths: containment", fun () ->
          check (PathPattern.isWithin (p "src/Quotes.Data/Logging/**") (p "src/Quotes.Data/**")) "nested"
          check (not (PathPattern.isWithin (p "src/Quotes*/**") (p "src/Quotes.Data/**"))) "prefix glob escapes"
          check (PathPattern.isWithin (p "src/Quotes.Data/Q.cs") (p "src/**/*.cs")) "exact in glob"

      // ---------- validator ----------
      "validator: example plan is valid", fun () -> equal [] (errorsOf plan) "errors"
      "validator: write outside element", fun () ->
          let es = errorsOf (withTask "T-002" (fun t -> { t with Writes = [ p "src/Quotes.Data/Hack.cs" ] }))
          check (es |> List.exists (function WriteOutsideElement _ -> true | _ -> false)) $"%A{es}"
      "validator: protected write", fun () ->
          let es = errorsOf (withTask "T-001" (fun t -> { t with Writes = p "registry/decisions/**" :: t.Writes }))
          check (es |> List.exists (function ProtectedWrite _ -> true | _ -> false)) $"%A{es}"
      "validator: every bound decision covered", fun () ->
          let es = errorsOf (withTask "T-001" (fun t -> { t with Covers = [] }))
          check (List.contains (UncoveredDecision(DecisionId "D-0042")) es) $"%A{es}"
      "validator: cover must touch a bound element", fun () ->
          let es = errorsOf (withTask "T-002" (fun t -> { t with Covers = [ DecisionId "D-0042" ] }))
          check (List.contains (CoverWithoutBinding(TaskId "T-002", DecisionId "D-0042")) es) $"%A{es}"
      "validator: self-seeded needs cleanup", fun () ->
          let strip = function Automated a -> Automated { a with Cleanup = None } | m -> m
          let es = errorsOf (withTask "T-001" (fun t -> { t with Verify = t.Verify |> List.map strip }))
          check (es |> List.exists (function SelfSeededWithoutCleanup _ -> true | _ -> false)) $"%A{es}"
      "validator: manual-only verify rejected", fun () ->
          let es = errorsOf (withTask "T-004" (fun t -> { t with Verify = t.Verify |> List.filter (function Manual _ -> true | _ -> false) }))
          check (List.contains (NoAutomatedVerify(TaskId "T-004")) es) $"%A{es}"
      "validator: dependency cycle and unknown dep", fun () ->
          let pl = withTask "T-001" (fun t -> { t with DependsOn = [ TaskId "T-004"; TaskId "T-999" ] })
          let es = errorsOf pl
          check (es |> List.exists (function DependencyCycle _ -> true | _ -> false)) $"cycle %A{es}"
          check (List.contains (UnknownDependency(TaskId "T-001", TaskId "T-999")) es) $"unknown %A{es}"
      "validator: handover mismatch", fun () ->
          let es = errorsOf { plan with HandoverDigest = "sha256:stale" }
          check (es |> List.exists (function HandoverMismatch _ -> true | _ -> false)) $"%A{es}"
      "validator: test writes must not overlap writes", fun () ->
          let es = errorsOf (withTask "T-001" (fun t -> { t with Writes = p "tests/Quotes.Data.Tests/**" :: t.Writes }))
          check (es |> List.exists (function TestWritesOverlapWrites(TaskId "T-001", _, _) -> true | _ -> false)) $"%A{es}"
      "validator: test write outside element", fun () ->
          let es = errorsOf (withTask "T-002" (fun t -> { t with TestWrites = p "tests/Quotes.Data.Tests/**" :: t.TestWrites }))
          check (List.contains (TestWriteOutsideElement(TaskId "T-002", p "tests/Quotes.Data.Tests/**")) es) $"%A{es}"
      "validator: invariant must run something under test writes or a protected path", fun () ->
          let es = errorsOf (withTask "T-002" (fun t -> { t with TestWrites = [] }))
          check (List.contains (InvariantNotUnderTestWrites(TaskId "T-002", "unit: retry from fresh read")) es) $"%A{es}"
      "validator: every error has a description", fun () ->
          let es = errorsOf { plan with Tasks = [] ; HandoverId = "x" }
          check (es |> List.forall (fun e -> (PlanError.describe e).Length > 10)) "descriptions"

      // ---------- dispatcher ----------
      "dispatcher: example waves", fun () ->
          let d = Dispatcher.dispatch validated.Value
          let shape = d.Waves |> List.map (fun w -> w.Teams |> List.map (fun t -> t.Tasks |> List.map (fun x -> x.Id.Value)))
          equal [ [ [ "T-001"; "T-004" ] ]; [ [ "T-002" ]; [ "T-003" ] ] ] shape "waves"
      "dispatcher: teams never overlap", fun () ->
          let teams = (Dispatcher.dispatch validated.Value).Waves |> List.collect (fun w -> w.Teams)
          for a in teams do
              for b in teams do
                  if a.Id <> b.Id then
                      check (not (a.Writes |> List.exists (fun wa -> b.Writes |> List.exists (PathPattern.mayOverlap wa)))) $"{a.Id} vs {b.Id}"
      "dispatcher: cross-team cycle collapses into one team", fun () ->
          // A1 -> B1 -> A2 where A1, A2 share files and B1 is elsewhere: A and B must be one team.
          let mk id writes deps =
              { (task "T-002") with Id = TaskId id; Writes = [ p writes ]; DependsOn = deps |> List.map TaskId; Covers = [] }
          let a1 = { mk "A1" "src/PricingApi/One.cs" [] with Covers = [] }
          let b1 = { (mk "B1" "src/ReferralWorker/X.cs" [ "A1" ]) with Elements = [ ElementId "Pricing Platform/Referral Worker" ]; TestWrites = [ p "tests/ReferralWorker.Tests/**" ]; Verify = (task "T-003").Verify }
          let a2 = mk "A2" "src/PricingApi/One.cs" [ "B1" ]
          let t1 = task "T-001"
          let pl = { plan with Tasks = [ t1; a1; b1; a2 ] }
          match PlanValidator.validate handover pl with
          | Error es -> failwithf "%A" es
          | Ok vp ->
              let d = Dispatcher.dispatch vp
              let team = d.Waves |> List.collect (fun w -> w.Teams) |> List.find (fun t -> t.Tasks |> List.exists (fun x -> x.Id = TaskId "A1"))
              equal [ "A1"; "B1"; "A2" ] (team.Tasks |> List.map (fun x -> x.Id.Value)) "one team, dependency order"

      // ---------- task state ----------
      "state: happy path", fun () ->
          let s =
              TaskState.replay limits [ TurnStarted Frontier; TurnEnded; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted [ outcome "a" 0 ] ]
          check (match s with Ok(Passed ev) -> ev.Attempt = 1 | _ -> false) $"%A{s}"
      "state: failed verify returns to Ready, then passes", fun () ->
          let s =
              TaskState.replay limits
                  [ TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted [ outcome "a" 1 ]
                    TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted [ outcome "a" 0 ] ]
          check (match s with Ok(Passed ev) -> ev.Attempt = 2 | _ -> false) $"%A{s}"
      "state: verify budget", fun () ->
          let s =
              TaskState.replay limits
                  [ TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted [ outcome "a" 1 ]
                    TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted [ outcome "a" 1 ] ]
          check (match s with Ok(Failed(VerifyBudgetExhausted _, _)) -> true | _ -> false) $"%A{s}"
      "state: turn budget", fun () ->
          let turn = [ TurnStarted Small; TurnEnded ]
          let s = TaskState.replay limits (List.concat [ turn; turn; turn; [ TurnStarted Small ] ])
          check (match s with Ok(Failed(TurnBudgetExhausted 3, _)) -> true | _ -> false) $"%A{s}"
      "state: escalation blocks and resumes", fun () ->
          let nd = { Question = "Retry limit for stale writes?"; Elements = []; Source = "agent" }
          let s = TaskState.replay limits [ TurnStarted Standard; NeedsDecisionRaised nd ]
          check (match s with Ok(Blocked _) -> true | _ -> false) "blocked"
          let s2 = TaskState.replay limits [ TurnStarted Standard; NeedsDecisionRaised nd; DecisionBound(DecisionId "D-0043"); TurnStarted Standard ]
          check (match s2 with Ok(Running(p, _)) -> p.Turns = 2 | _ -> false) $"resumed %A{s2}"
      "state: illegal transitions rejected", fun () ->
          check (Result.isError (TaskState.transition limits TaskState.queued (VerifyCompleted [ outcome "a" 0 ]))) "no pass without verify"
          check (Result.isError (TaskState.transition limits TaskState.queued TurnEnded)) "no turn end before start"
          check (Result.isError (PassedEvidence.Create(1, []))) "no empty evidence"
      "state: events round-trip through the ledger codec", fun () ->
          let evs =
              [ TurnStarted Frontier; TurnEnded
                NeedsDecisionRaised { Question = "q"; Elements = [ ElementId "e" ]; Source = "configurator" }
                DecisionBound(DecisionId "D-1"); VerifyRequested; VerifyCompleted [ outcome "x" 0 ]; Abandon "stop" ]
          for e in evs do
              equal (Ok e) (Json.decodeTaskEvent (Json.encodeTaskEvent e)) "round trip"

      // ---------- merge ----------
      "merge: high-risk element and manual check need approvals", fun () ->
          let d = Dispatcher.dispatch validated.Value
          let v =
              Merge.decide Merge.defaultPolicy
                  { Teams = d.Waves |> List.collect (fun w -> w.Teams); Handover = handover; ChangedLines = 120
                    ChangedFiles = [ "src/Quotes.Data/Quote.cs" ] }
          equal TwoApprovals v.Tier "tier"
          check (v.Reasons |> List.exists (fun r -> r.Contains "manual")) "manual reason"
      "merge: file outside declared writes escalates", fun () ->
          let team = { Id = TeamId "t"; Tasks = [ task "T-002" ]; Writes = [ p "src/PricingApi/**" ]; Elements = [] }
          let v = Merge.decide Merge.defaultPolicy { Teams = [ team ]; Handover = handover; ChangedLines = 5; ChangedFiles = [ "Directory.Build.props" ] }
          equal TwoApprovals v.Tier "tier"
      "merge: small low-risk change auto-merges", fun () ->
          let h = { handover with Elements = handover.Elements |> List.map (fun e -> { e with Risk = Low }) }
          let team = { Id = TeamId "t"; Tasks = [ task "T-002" ]; Writes = [ p "src/PricingApi/**" ]; Elements = [ ElementId "Pricing Platform/Pricing API" ] }
          let v = Merge.decide Merge.defaultPolicy { Teams = [ team ]; Handover = h; ChangedLines = 40; ChangedFiles = [ "src/PricingApi/Q.cs" ] }
          equal AutoMerge v.Tier "tier"

      // ---------- json ----------
      "json: malformed plan reports every problem at once", fun () ->
          let bad = """{ "handover": {"id":"x","digest":"y"}, "tasks": [ {"id":"A"}, {"id":"B","title":"t","instructions":"i","writes":["../x"]} ] }"""
          match Json.parsePlan bad with
          | Ok _ -> failwith "should fail"
          | Error es -> check (es.Length >= 2) $"%A{es}"
      "json: handover round-trips through writeHandover", fun () ->
          match Json.parseHandover (Json.writeHandover handover) with
          | Ok h -> equal handover h "round trip"
          | Error es -> failwithf "%A" es
      "json: dispatch serialises", fun () ->
          let text = Json.writeDispatch (Dispatcher.dispatch validated.Value)
          check (text.Contains "team-T-001") text ]

[<EntryPoint>]
let main _ =
    let mutable failed = 0
    for (name, body) in tests do
        try
            body ()
            printfn "  pass  %s" name
        with
        | AssertFailed m ->
            failed <- failed + 1
            printfn "  FAIL  %s\n        %s" name m
        | e ->
            failed <- failed + 1
            printfn "  FAIL  %s\n        %s" name (e.ToString())
    printfn "\n%d passed, %d failed" (tests.Length - failed) failed
    if failed = 0 then 0 else 1
