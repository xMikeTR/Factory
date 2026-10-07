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
let red = match RedEvidence.Create([ outcome "a" 1 ], AssertionFailure) with Ok r -> r | Error e -> failwith e
let authored = TestsAuthored red
let stashedRed = [ outcome "a" 1 ]

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
              TaskState.replay limits
                  [ TurnStarted Frontier; TurnEnded; authored; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 0 ], stashedRed) ]
          check (match s with Ok(Passed ev) -> ev.Attempt = 1 && ev.Red = red && ev.StashedImpl = stashedRed | _ -> false) $"%A{s}"
      "state: failed verify returns to Ready, then passes", fun () ->
          let s =
              TaskState.replay limits
                  [ authored; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 1 ], [])
                    TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 0 ], stashedRed) ]
          check (match s with Ok(Passed ev) -> ev.Attempt = 2 | _ -> false) $"%A{s}"
      "state: verify budget", fun () ->
          let s =
              TaskState.replay limits
                  [ authored; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 1 ], [])
                    TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 1 ], []) ]
          check (match s with Ok(Failed(VerifyBudgetExhausted _, _)) -> true | _ -> false) $"%A{s}"
      "state: turn budget", fun () ->
          let turn = [ TurnStarted Small; TurnEnded ]
          let s = TaskState.replay limits (List.concat [ turn; turn; turn; [ TurnStarted Small ] ])
          check (match s with Ok(Failed(TurnBudgetExhausted 3, _)) -> true | _ -> false) $"%A{s}"
      "state: escalation blocks and resumes in the same phase", fun () ->
          let nd = { Question = "Retry limit for stale writes?"; Elements = []; Source = "agent" }
          let s = TaskState.replay limits [ TurnStarted Standard; NeedsDecisionRaised nd ]
          check (match s with Ok(Blocked(_, TestAuthor, _)) -> true | _ -> false) $"blocked %A{s}"
          let s2 = TaskState.replay limits [ authored; TurnStarted Standard; NeedsDecisionRaised nd; DecisionBound(DecisionId "D-0043"); TurnStarted Standard ]
          check (match s2 with Ok(Running(p, Implementer _, _)) -> p.Turns = 2 | _ -> false) $"resumed %A{s2}"
      "state: a task starts with the TestAuthor and moves on only with red evidence", fun () ->
          check (match TaskState.queued with Ready(_, TestAuthor) -> true | _ -> false) "queued phase"
          let s = TaskState.replay limits [ TurnStarted Standard; TurnEnded; authored ]
          check (match s with Ok(Ready(p, Implementer r)) -> p.Turns = 1 && r = red | _ -> false) $"%A{s}"
      "state: no verify in the TestAuthor phase, no second TestsAuthored", fun () ->
          check (Result.isError (TaskState.transition limits TaskState.queued VerifyRequested)) "verify before tests"
          check (Result.isError (TaskState.replay limits [ authored; authored ])) "authored twice"
          check (Result.isError (TaskState.replay limits [ TurnStarted Small; authored ])) "authored mid-turn"
      "state: vacuous tests send the task back to the TestAuthor", fun () ->
          let s = TaskState.replay limits [ authored; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 0 ], [ outcome "a" 0 ]) ]
          check (match s with Ok(Ready(p, TestAuthor)) -> p.VerifyAttempts = 1 | _ -> false) $"%A{s}"
      "state: the Implementer hands broken tests back to the TestAuthor, within the turn budget", fun () ->
          let back = ReturnedToTestAuthor "the P model's generated project sits inside the test project"
          let s = TaskState.replay limits [ authored; TurnStarted Standard; TurnEnded; back ]
          check (match s with Ok(Ready(p, TestAuthor)) -> p.Turns = 1 | _ -> false) $"%A{s}"
          check (Result.isError (TaskState.transition limits TaskState.queued back)) "the TestAuthor cannot hand back to itself"
          check (Result.isError (TaskState.replay limits [ authored; TurnStarted Standard; back ])) "not mid-turn"
      "evidence: red refuses a run where nothing failed", fun () ->
          check (Result.isError (RedEvidence.Create([ outcome "a" 0; outcome "b" 0 ], AssertionFailure))) "all zero"
          check (Result.isError (RedEvidence.Create([], BuildFailure))) "empty"
          check (Result.isOk (RedEvidence.Create([ outcome "a" 0; outcome "b" 1 ], AssertionFailure))) "one red is enough"
      "red run: assertion and compile failures are red, each with its kind", fun () ->
          let failing name tail = { outcome name 1 with OutputTail = tail }
          equal (Ok AssertionFailure) (RedRun.classify [ failing "a" "Failed!  - Failed: 7"; outcome "b" 0 ]) "assertions"
          equal (Ok BuildFailure) (RedRun.classify [ failing "a" "Quote.cs(3,5): error CS0246: QuoteStatus"; failing "b" "Failed: 1" ]) "compile"
      "red run: a check the sandbox could not run is no evidence, whatever the other checks did", fun () ->
          let failing name code tail = { outcome name code with OutputTail = tail }
          let nuget = failing "P model check" 1 "Optimistic.csproj : error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json."
          equal (Error [ "P model check" ]) (RedRun.classify [ failing "concurrency" 1 "Failed!  - Failed: 7"; nuget ]) "NU1301 beside a real red"
          equal (Error [ "pobserve" ]) (RedRun.classify [ failing "pobserve" 127 "bash: pobserve: command not found" ]) "missing tool"
      "evidence: passed needs green and a stashed-impl run that fails", fun () ->
          check (Result.isError (PassedEvidence.Create(1, [], red, stashedRed))) "no empty evidence"
          check (Result.isError (PassedEvidence.Create(1, [ outcome "a" 1 ], red, stashedRed))) "not green"
          check (Result.isError (PassedEvidence.Create(1, [ outcome "a" 0 ], red, [ outcome "a" 0 ]))) "stash all green"
          check (Result.isError (PassedEvidence.Create(1, [ outcome "a" 0 ], red, []))) "stash missing"
          check (Result.isOk (PassedEvidence.Create(1, [ outcome "a" 0 ], red, stashedRed))) "all three gates"
      "state: illegal transitions rejected", fun () ->
          check (Result.isError (TaskState.transition limits TaskState.queued (VerifyCompleted([ outcome "a" 0 ], stashedRed)))) "no pass without verify"
          check (Result.isError (TaskState.transition limits TaskState.queued TurnEnded)) "no turn end before start"
      "state: events round-trip through the ledger codec", fun () ->
          let evs =
              [ TurnStarted Frontier; TurnEnded
                NeedsDecisionRaised { Question = "q"; Elements = [ ElementId "e" ]; Source = "configurator" }
                DecisionBound(DecisionId "D-1"); authored; ReturnedToTestAuthor "tests broken"; VerifyRequested; VerifyCompleted([ outcome "x" 0 ], stashedRed); Abandon "stop" ]
          for e in evs do
              equal (Ok e) (Json.decodeTaskEvent (Json.encodeTaskEvent e)) "round trip"

      // ---------- merge ----------
      "merge: high-risk element needs two approvals and a manual check is a pending block", fun () ->
          let d = Dispatcher.dispatch validated.Value
          let v =
              Merge.decide Merge.defaultPolicy
                  { Teams = d.Waves |> List.collect (fun w -> w.Teams); Handover = handover; ChangedLines = 120
                    ChangedFiles = [ "src/Quotes.Data/Quote.cs" ] }
          equal TwoApprovals v.Tier "tier"
          check (v.Blocks |> List.exists (function ManualVerifyPending _ -> true | _ -> false)) $"%A{v.Blocks}"
      "merge: a file outside every team's writes is a scope leak block", fun () ->
          let team = { Id = TeamId "t"; Tasks = [ task "T-002" ]; Writes = [ p "src/PricingApi/**" ]; Elements = [] }
          let v = Merge.decide Merge.defaultPolicy { Teams = [ team ]; Handover = handover; ChangedLines = 5; ChangedFiles = [ "Directory.Build.props" ] }
          equal [ ScopeLeak "Directory.Build.props" ] v.Blocks "blocks"
      "merge: a change under a protected path is its own block", fun () ->
          let team = { Id = TeamId "t"; Tasks = [ task "T-002" ]; Writes = [ p "src/PricingApi/**" ]; Elements = [] }
          let h = { handover with ProtectedPaths = [ p ".github/**" ] }
          let v = Merge.decide Merge.defaultPolicy { Teams = [ team ]; Handover = h; ChangedLines = 5; ChangedFiles = [ ".github/workflows/ci.yml" ] }
          equal [ ProtectedPathChanged ".github/workflows/ci.yml" ] v.Blocks "blocks"
      "merge: small low-risk change auto-merges", fun () ->
          let h = { handover with Elements = handover.Elements |> List.map (fun e -> { e with Risk = Low }) }
          let team = { Id = TeamId "t"; Tasks = [ task "T-002" ]; Writes = [ p "src/PricingApi/**" ]; Elements = [ ElementId "Pricing Platform/Pricing API" ] }
          let v = Merge.decide Merge.defaultPolicy { Teams = [ team ]; Handover = h; ChangedLines = 40; ChangedFiles = [ "src/PricingApi/Q.cs" ] }
          equal AutoMerge v.Tier "tier"
          equal [] v.Blocks "no blocks"
      "merge: every block stops the merge in both modes", fun () ->
          for block in [ ScopeLeak "x"; ProtectedPathChanged ".github/x"; ManualVerifyPending "T-001: eyeball" ] do
              for mode in [ Auto; Tiered [ "alice" ] ] do
                  let v = { Tier = AutoMerge; Reasons = []; Blocks = [ block ] }
                  equal (MergeAction.Blocked [ block ]) (Merge.action { Merge.defaultPolicy with Enforcement = mode } v) $"{block} {mode}"
      "merge: auto mode merges on green whatever the tier", fun () ->
          let v = { Tier = TwoApprovals; Reasons = [ "high risk" ]; Blocks = [] }
          equal MergeAction.MergeNow (Merge.action { Merge.defaultPolicy with Enforcement = Auto } v) "auto"
      "merge: tiered mode merges at AutoMerge and waits for approvers above it", fun () ->
          let tiered = { Merge.defaultPolicy with Enforcement = Tiered [ "alice" ] }
          equal MergeAction.MergeNow (Merge.action tiered { Tier = AutoMerge; Reasons = []; Blocks = [] }) "auto tier"
          equal (MergeAction.AwaitApproval(OneApproval, [ "alice" ])) (Merge.action tiered { Tier = OneApproval; Reasons = []; Blocks = [] }) "one"

      "approve: distinct allow-listed logins count; others and plain comments do not", fun () ->
          let c a b = { Author = a; Body = b }
          let comments = [ c "alice" "/approve looks right"; c "mallory" "/approve"; c "bob" "nice"; c "ALICE" "/approve again" ]
          let a = Merge.approvals TwoApprovals [ "alice"; "bob" ] comments
          equal false a.Approved "one distinct approver"
          equal [ "alice" ] a.By "by"
          equal 1 a.Missing "missing"
          let b = Merge.approvals TwoApprovals [ "alice"; "bob" ] (comments @ [ c "bob" "/approve ok" ])
          equal true b.Approved "two"
          equal false b.SingleOperator "not single"
      "approve: a single operator approves twice with two stated reasons, flagged", fun () ->
          let c b = { Author = "mike"; Body = b }
          let once = Merge.approvals TwoApprovals [ "mike" ] [ c "/approve schema reviewed"; c "/approve schema reviewed" ]
          equal false once.Approved "same reason twice is one approval"
          let noReason = Merge.approvals TwoApprovals [ "mike" ] [ c "/approve"; c "/approve" ]
          equal false noReason.Approved "reasons are required"
          let twice = Merge.approvals TwoApprovals [ "mike" ] [ c "/approve schema reviewed"; c "/approve migration dry-run ok" ]
          equal true twice.Approved "approved"
          equal true twice.SingleOperator "flagged"
          equal false (Merge.approvals OneApproval [ "mike" ] [ c "/approve" ]).SingleOperator "one approval is not an override"
      "approve: an allow-listed /verified comment clears that manual check's block", fun () ->
          let v = { Tier = OneApproval; Reasons = []; Blocks = [ ManualVerifyPending "T-001: eyeball"; ScopeLeak "x" ] }
          let comments = [ { Author = "eve"; Body = "/verified T-001: eyeball" } ]
          equal v.Blocks (Merge.recordVerified [ "mike" ] comments v).Blocks "not allow-listed"
          let ok = Merge.recordVerified [ "mike" ] [ { Author = "mike"; Body = "/verified T-001: eyeball" } ] v
          equal [ ScopeLeak "x" ] ok.Blocks "manual cleared, scope leak stays"

      // ---------- regression ----------
      "regression: a test that passed before and fails now is a regression", fun () ->
          let r name passed = { Name = name; Passed = passed }
          equal [ "b" ] (Regression.regressions [ r "a" true; r "b" true ] [ r "a" true; r "b" false ]) "p2p"
      "regression: tests that failed before, or are new, are not regressions", fun () ->
          let r name passed = { Name = name; Passed = passed }
          equal [] (Regression.regressions [ r "a" false ] [ r "a" false; r "new" false ]) "f2f and new"
      "regression: a passing test missing now counts (deleted, or the suite did not build)", fun () ->
          let r name passed = { Name = name; Passed = passed }
          equal [ "a"; "b" ] (Regression.regressions [ r "a" true; r "b" true ] []) "missing"
      "regression: a duplicated name passes only if every run of it passes", fun () ->
          let r name passed = { Name = name; Passed = passed }
          equal [] (Regression.regressions [ r "a" true; r "a" false ] [ r "a" false ]) "flaky in baseline is not p2p"
          equal [ "a" ] (Regression.regressions [ r "a" true ] [ r "a" true; r "a" false ]) "one failing run now"

      // ---------- handover closure ----------
      "closure: delivered only when merged and every check of every bound decision passed", fun () ->
          let d1 = { Id = DecisionId "D-0001"; Statement = "s"; Binds = []; Checks = [ { Kind = "p"; Run = "p check -tc a" }; { Kind = "p"; Run = "p check -tc b" } ] }
          let d2 = { Id = DecisionId "D-0002"; Statement = "s"; Binds = []; Checks = [ { Kind = "p"; Run = "p check -tc c" } ] }
          let r d run passed = { Decision = DecisionId d; Check = run; Passed = passed }
          let all = [ r "D-0001" "p check -tc a" true; r "D-0001" "p check -tc b" true; r "D-0002" "p check -tc c" true ]
          equal HandoverOutcome.Delivered (HandoverClosure.outcome [ d1; d2 ] true all) "all pass"
          equal HandoverOutcome.Abandoned (HandoverClosure.outcome [ d1; d2 ] false all) "not merged"
          equal HandoverOutcome.Abandoned (HandoverClosure.outcome [ d1; d2 ] true (List.take 2 all)) "a check never ran"
          equal HandoverOutcome.Abandoned (HandoverClosure.outcome [ d1; d2 ] true (all @ [ r "D-0002" "p check -tc c" false ])) "latest run failed"
          equal HandoverOutcome.Delivered (HandoverClosure.outcome [ d1; d2 ] true (r "D-0001" "p check -tc a" false :: all)) "failed then passed"
      "closure: a decision with no checks does not block delivery", fun () ->
          let d = { Id = DecisionId "D-0001"; Statement = "s"; Binds = []; Checks = [] }
          equal HandoverOutcome.Delivered (HandoverClosure.outcome [ d ] true []) "no checks"

      // ---------- regeneration ----------
      "regeneration: an unchanged handover re-runs nothing", fun () ->
          let r = Regeneration.between handover handover plan
          equal [] r.Changed "changed"
          equal [] r.Rerun "rerun"
      "regeneration: a changed decision re-runs only the tasks on the elements it binds", fun () ->
          let after =
              { handover with
                  Decisions = handover.Decisions |> List.map (fun d -> { d with Statement = d.Statement + " Retry once on conflict." }) }
          let r = Regeneration.between handover after plan
          equal [ DecisionId "D-0042" ] r.Changed "changed"
          equal [ TaskId "T-001"; TaskId "T-004" ] r.Rerun "rerun (Quotes DB tasks; T-002/T-003 depend on T-001 but are not bound)"
      "regeneration: a rebound decision re-runs tasks on the old and the new elements", fun () ->
          let after =
              { handover with
                  Decisions = handover.Decisions |> List.map (fun d -> { d with Binds = [ ElementId "Pricing Platform/Referral Worker" ] }) }
          equal [ TaskId "T-001"; TaskId "T-003"; TaskId "T-004" ] (Regeneration.between handover after plan).Rerun "rerun"
      "regeneration: an added or removed decision counts as changed", fun () ->
          let added =
              { Id = DecisionId "D-0043"; Statement = "Quotes are priced in the cedant's currency."
                Binds = [ ElementId "Pricing Platform/Pricing API" ]; Checks = [] }
          let after = { handover with Decisions = handover.Decisions @ [ added ] }
          let r = Regeneration.between handover after plan
          equal [ DecisionId "D-0043" ] r.Changed "added"
          equal [ TaskId "T-002" ] r.Rerun "rerun"
          equal [ DecisionId "D-0043" ] (Regeneration.between after handover plan).Changed "removed"

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
