module Factory.Core.Tests.WorkTests

open Factory.Core
open Factory.Core.Tests.Support

let p = PathPattern.createOrFail
let draft =
    { Id = "W-1"; Requirement = Some "REQ-031"; Elements = [ "Pricing/Quote Form" ]; Repo = None; Target = None
      DependsOn = []; Writes = [ "src/Quotes/**" ]; DoneWhen = [ "gate", "dotnet test" ] }
let docDraft = { draft with Requirement = None; Elements = []; Writes = []; Target = Some("document", "Q3 pricing write-up"); DoneWhen = [ "checklist", "audience named" ] }

let created d = match WorkItem.create d with Ok i -> i | Error es -> failwithf "%A" es
let errorsOf d = match WorkItem.create d with Ok _ -> [] | Error es -> es
let repoItem = created draft
let docItem = created docDraft
let run r = RunId r
let at = { Handover = "20261013-aa"; DesignVersion = "r-1" }

let allChecks = [ "gate", "g"; "checklist", "c"; "review", "r"; "judge", "j" ]
/// Every done_when list of up to three entries over the four kinds.
let allDoneWhen =
    [ yield []
      for a in allChecks do
          yield [ a ]
          for b in allChecks do
              yield [ a; b ]
              for c in allChecks do yield [ a; b; c ] ]

let facts = { Commit = Some "abc1234"; OnMain = Some true; GateRuns = [ { Gate = "dotnet test"; Commit = Some "abc1234"; Passed = true; Dirty = false } ]; Ticks = []; ParityRuns = []; ParityScenarios = [] }
let parityItem v = created { draft with DoneWhen = [ "gate", "dotnet test"; "parity", v ] }
let parityRun s outcome = { Scenario = s; Commit = "abc1234"; Outcome = outcome }

let tests: (string * (unit -> unit)) list =
    [ "work: a repo item and a non-repo item are valid", fun () ->
          equal (WorkItemId "W-1") repoItem.Id "id"
          check (match repoItem.Target with Repo("REQ-031", [ e ], None) -> e.Value = "Pricing/Quote Form" | _ -> false) $"%A{repoItem.Target}"
          check (match docItem.Target with Document "Q3 pricing write-up" -> true | _ -> false) $"%A{docItem.Target}"
      "work: name a requirement or a target, never both and never neither", fun () ->
          check (List.contains NeedsRequirementOrTarget (errorsOf { draft with Target = Some("document", "x") })) "both"
          check (List.contains NeedsRequirementOrTarget (errorsOf { draft with Requirement = None })) "neither"
      "work: a repo item needs elements and a gate", fun () ->
          check (List.contains RepoWithoutElements (errorsOf { draft with Elements = [] })) "elements"
          check (List.contains RepoWithoutGate (errorsOf { draft with DoneWhen = [ "checklist", "c" ] })) "gate"
      "work: a judge is never the only definition of done", fun () ->
          check (List.contains JudgeOnly (errorsOf { docDraft with DoneWhen = [ "judge", "clarity" ] })) "judge only"
          equal [] (errorsOf { docDraft with DoneWhen = [ "judge", "clarity"; "review", "Dana" ] }) "judge next to a review"
      "work: empty definition of done, unknown kinds and blank entries are named", fun () ->
          check (List.contains EmptyDoneWhen (errorsOf { draft with DoneWhen = [] })) "empty"
          check (List.contains (UnknownCheckKind(1, "magic")) (errorsOf { draft with DoneWhen = [ "magic", "x"; "gate", "g" ] })) "unknown"
          check (List.contains (BlankCheck(1, "gate")) (errorsOf { draft with DoneWhen = [ "gate", "  " ] })) "blank"
      "work: every error is collected, not the first", fun () ->
          let es = errorsOf { draft with Id = ""; Elements = []; Writes = [ "../x" ]; DoneWhen = [] }
          check (List.length es >= 4) $"%A{es}"
          check (es |> List.exists (function WriteNotAPathPattern _ -> true | _ -> false)) $"%A{es}"
      "work: exhaustive over done_when lists, valid exactly when the rules hold", fun () ->
          for dw in allDoneWhen do
              let kinds = dw |> List.map fst
              let judgeOnly = not kinds.IsEmpty && kinds |> List.forall ((=) "judge")
              check (WorkItem.create { docDraft with DoneWhen = dw } |> Result.isOk = (not kinds.IsEmpty && not judgeOnly)) $"document %A{dw}"
              check (WorkItem.create { draft with DoneWhen = dw } |> Result.isOk = (List.contains "gate" kinds)) $"repo %A{dw}"
      // ---------- evidence: done covers every check ----------
      "evidence: a passing gate at a commit on main covers a repo item", fun () ->
          check (Result.isOk (Evidence.create repoItem facts)) "covered"
      "evidence: no passing run, a failing last run, a dirty run or another commit are not evidence", fun () ->
          let bad r = match Evidence.create repoItem { facts with GateRuns = [ r ] } with Error _ -> true | Ok _ -> false
          let ok = facts.GateRuns.Head
          check (bad { ok with Passed = false }) "failed"
          check (bad { ok with Dirty = true }) "dirty"
          check (bad { ok with Commit = Some "other" }) "other commit"
          check (Result.isError (Evidence.create repoItem { facts with GateRuns = [] })) "never ran"
      "evidence: the last run of a gate decides", fun () ->
          let ok = facts.GateRuns.Head
          check (Result.isError (Evidence.create repoItem { facts with GateRuns = [ ok; { ok with Passed = false } ] })) "fail last"
          check (Result.isOk (Evidence.create repoItem { facts with GateRuns = [ { ok with Passed = false }; ok ] })) "pass last"
      "evidence: a commit not on main, or one that could not be checked, is not done", fun () ->
          check (Result.isError (Evidence.create repoItem { facts with OnMain = Some false })) "not on main"
          check (Result.isError (Evidence.create repoItem { facts with OnMain = None })) "unknown"
          check (Result.isError (Evidence.create repoItem { facts with Commit = None })) "no commit"
      "evidence: checklists and reviews need a tick, a judge needs nothing", fun () ->
          let item = created { docDraft with DoneWhen = [ "checklist", "audience named"; "review", "Dana"; "judge", "clarity" ] }
          let none = { facts with Commit = None; OnMain = None; GateRuns = [] }
          check (Result.isError (Evidence.create item none)) "unticked"
          check (Result.isOk (Evidence.create item { none with Ticks = [ "checklist:audience named"; "review:Dana" ] })) "ticked"
      // ---------- parity: a recorded parity_result at the done commit, never a claim ----------
      "work: a parity entry names a scenario or all, and only a repo item has one", fun () ->
          check (match (parityItem "quote-list").DoneWhen.ToList with [ Gate _; Parity "quote-list" ] -> true | _ -> false) "scenario"
          equal "parity:all" (Check.key (Parity "all")) "key"
          check (List.contains (BlankCheck(2, "parity")) (errorsOf { draft with DoneWhen = [ "gate", "g"; "parity", " " ] })) "blank"
          check (List.contains ParityWithoutRepo (errorsOf { docDraft with DoneWhen = [ "checklist", "c"; "parity", "all" ] })) "not a repo item"
          check (List.contains RepoWithoutGate (errorsOf { draft with DoneWhen = [ "parity", "all" ] })) "parity is not a gate"
      "evidence: parity needs a passing result for the scenario at the done commit", fun () ->
          let item = parityItem "quote-list"
          check (Result.isOk (Evidence.create item { facts with ParityRuns = [ parityRun "quote-list" ParityPass ] })) "pass"
          check (Result.isError (Evidence.create item facts)) "never recorded"
          check (Result.isError (Evidence.create item { facts with ParityRuns = [ parityRun "quote-list" ParityFail ] })) "fail"
          check (Result.isError (Evidence.create item { facts with ParityRuns = [ parityRun "quote-list" ParityCouldNotRun ] })) "could not run is not a pass"
          check (Result.isError (Evidence.create item { facts with ParityRuns = [ { parityRun "quote-list" ParityPass with Commit = "other" } ] })) "other commit"
          check (Result.isError (Evidence.create item { facts with ParityRuns = [ parityRun "other-screen" ParityPass ] })) "other scenario"
      "evidence: the last parity result for a scenario at the commit decides", fun () ->
          let item = parityItem "quote-list"
          check (Result.isOk (Evidence.create item { facts with ParityRuns = [ parityRun "quote-list" ParityFail; parityRun "quote-list" ParityPass ] })) "pass last"
          check (Result.isError (Evidence.create item { facts with ParityRuns = [ parityRun "quote-list" ParityPass; parityRun "quote-list" ParityFail ] })) "fail last"
      "evidence: parity all needs every registry scenario to pass, and fails when there are none", fun () ->
          let item = parityItem "all"
          let scenarios = [ "a"; "b" ]
          check (Result.isOk (Evidence.create item { facts with ParityScenarios = scenarios; ParityRuns = [ parityRun "a" ParityPass; parityRun "b" ParityPass ] })) "all pass"
          match Evidence.create item { facts with ParityScenarios = scenarios; ParityRuns = [ parityRun "a" ParityPass ] } with
          | Error es -> check (es |> List.exists (fun e -> e.Contains "b")) $"names the missing scenario: %A{es}"
          | Ok _ -> failwith "b never ran"
          check (Result.isError (Evidence.create item { facts with ParityRuns = [ parityRun "a" ParityPass ] })) "no scenarios in the registry"
      // ---------- state machine ----------
      "state: the happy path", fun () ->
          let ev = match Evidence.create repoItem facts with Ok e -> e | Error es -> failwithf "%A" es
          let steps = [ WorkEvent.Frozen at; WorkEvent.Pulled(run "r1"); WorkEvent.AwaitedHuman "pick option"; WorkEvent.Resumed; WorkEvent.Finished ev ]
          let final = steps |> List.fold (fun s e -> match s with Ok st -> WorkState.apply st e | err -> err) (Ok WorkState.Backlog)
          check (match final with Ok(WorkState.Done _) -> true | _ -> false) $"%A{final}"
      "state: awaiting a human keeps the run, and counts as in flight", fun () ->
          let s = WorkState.apply (WorkState.Active(run "r1")) (WorkEvent.AwaitedHuman "q")
          equal (Ok(WorkState.AwaitingHuman(run "r1", "q"))) s "state"
          equal (Some(run "r1")) (WorkState.runOf (WorkState.AwaitingHuman(run "r1", "q"))) "run"
          check (WorkState.inFlight (WorkState.AwaitingHuman(run "r1", "q"))) "in flight"
          check (not (WorkState.inFlight (WorkState.Ready at))) "ready is not"
      "state: every transition outside the table is refused", fun () ->
          let ev = match Evidence.create repoItem facts with Ok e -> e | Error es -> failwithf "%A" es
          let states = [ WorkState.Backlog; WorkState.Ready at; WorkState.Active(run "r1"); WorkState.AwaitingHuman(run "r1", "q"); WorkState.Done ev; WorkState.Blocked "why" ]
          let events = [ WorkEvent.Frozen at; WorkEvent.Pulled(run "r2"); WorkEvent.AwaitedHuman "q"; WorkEvent.Resumed; WorkEvent.Finished ev; WorkEvent.Rechecked "design moved"; WorkEvent.BlockedBy "why"; WorkEvent.Unblocked ]
          let allowed s e =
              match s, e with
              | WorkState.Backlog, WorkEvent.Frozen _ | WorkState.Ready _, WorkEvent.Pulled _ | WorkState.Active _, WorkEvent.AwaitedHuman _ | WorkState.AwaitingHuman _, WorkEvent.Resumed | WorkState.Active _, WorkEvent.Finished _ -> true
              | (WorkState.Backlog | WorkState.Ready _), WorkEvent.Rechecked _ -> true
              | (WorkState.Backlog | WorkState.Ready _ | WorkState.Active _ | WorkState.AwaitingHuman _), WorkEvent.BlockedBy _ -> true
              | WorkState.Blocked _, WorkEvent.Unblocked -> true
              | _ -> false
          for s in states do
              for e in events do
                  check (Result.isOk (WorkState.apply s e) = allowed s e) $"%A{s} + %A{e}"
      "state: a recheck sends a frozen item back to the backlog, a done item stays done", fun () ->
          equal (Ok WorkState.Backlog) (WorkState.apply (WorkState.Ready at) (WorkEvent.Rechecked "element moved")) "ready"
          let ev = match Evidence.create repoItem facts with Ok e -> e | Error es -> failwithf "%A" es
          check (Result.isError (WorkState.apply (WorkState.Done ev) (WorkEvent.Rechecked "x"))) "done"
      // ---------- conflicts, pull scheduler, re-check ----------
      "conflict: shared writes, shared element, same target; unrelated items do not", fun () ->
          let w id elements writes = created { draft with Id = id; Elements = elements; Writes = writes }
          let a = w "A" [ "UI" ] [ "src/Quotes/**" ]
          equal [ SharedWrites(p "src/Quotes/**", p "src/Quotes/Data/**") ] (Scheduler.conflict a (w "B" [ "Api" ] [ "src/Quotes/Data/**" ]) |> Option.toList) "writes"
          equal [ SharedElement(ElementId "UI") ] (Scheduler.conflict a (w "C" [ "UI" ] [ "src/Other/**" ]) |> Option.toList) "element"
          equal [] (Scheduler.conflict a (w "D" [ "Api" ] [ "src/Api/**" ]) |> Option.toList) "none"
          let d1 = created { docDraft with Id = "P" }
          equal [ SameTarget "document Q3 pricing write-up" ] (Scheduler.conflict d1 (created { docDraft with Id = "Q" }) |> Option.toList) "same document"
          equal [] (Scheduler.conflict d1 (created { docDraft with Id = "R"; Target = Some("document", "other") }) |> Option.toList) "another document"
      "scheduler: pulls ready items in priority then age order, up to the free slots", fun () ->
          let item id = created { docDraft with Id = id; Target = Some("document", id) }
          let ready id prio day = { Item = item id; State = WorkState.Ready at; Priority = prio; ReadyAt = System.DateTimeOffset(2026, 10, day, 0, 0, 0, System.TimeSpan.Zero) }
          let plan = Scheduler.plan { Candidates = [ ready "old" 0 1; ready "urgent" 5 3; ready "new" 0 2 ]; WipLimit = 2; Budget = None; Stale = Map.empty }
          equal [ "urgent"; "old" ] (plan.Pull |> List.map (fun i -> i.Id.Value)) "order"
          equal [ WorkItemId "new", WipFull(0, 2) ] plan.Deferred "the rest wait for a slot"
      "scheduler: awaiting a human holds a slot", fun () ->
          let item id = created { docDraft with Id = id; Target = Some("document", id) }
          let c id st = { Item = item id; State = st; Priority = 0; ReadyAt = System.DateTimeOffset.UnixEpoch }
          let plan = Scheduler.plan { Candidates = [ c "a" (WorkState.AwaitingHuman(run "r1", "q")); c "b" (WorkState.Active(run "r2")); c "c" (WorkState.Ready at) ]; WipLimit = 2; Budget = None; Stale = Map.empty }
          equal [] plan.Pull "full"
          equal [ WorkItemId "c", WipFull(2, 2) ] plan.Deferred "why"
      "scheduler: a conflicting item stays ready and the next non-conflicting one is pulled", fun () ->
          let w id el = created { draft with Id = id; Elements = [ el ]; Writes = [ $"src/{el}/**" ] }
          let c it st prio = { Item = it; State = st; Priority = prio; ReadyAt = System.DateTimeOffset.UnixEpoch }
          let active = w "active" "UI"
          let plan = Scheduler.plan { Candidates = [ c active (WorkState.Active(run "r1")) 0; c (w "clash" "UI") (WorkState.Ready at) 9; c (w "free" "Api") (WorkState.Ready at) 0 ]; WipLimit = 3; Budget = None; Stale = Map.empty }
          equal [ "free" ] (plan.Pull |> List.map (fun i -> i.Id.Value)) "pulled"
          check (match plan.Deferred with [ id, ConflictsWith [ { With = WorkItemId "active" } ] ] -> id = WorkItemId "clash" | _ -> false) $"%A{plan.Deferred}"
      "scheduler: two ready items that overlap are not both pulled", fun () ->
          let w id = created { draft with Id = id }
          let c it = { Item = it; State = WorkState.Ready at; Priority = 0; ReadyAt = System.DateTimeOffset.UnixEpoch }
          let plan = Scheduler.plan { Candidates = [ c (w "one"); c (w "two") ]; WipLimit = 3; Budget = None; Stale = Map.empty }
          equal 1 plan.Pull.Length "one"
      "scheduler: an unmet dependency, a stale freeze and an exhausted budget each defer with their reason", fun () ->
          let c it st = { Item = it; State = st; Priority = 0; ReadyAt = System.DateTimeOffset.UnixEpoch }
          let doc id deps = created { docDraft with Id = id; Target = Some("document", id); DependsOn = deps }
          let prereq = doc "pre" []
          let plan =
              Scheduler.plan
                  { Candidates = [ c prereq (WorkState.Backlog); c (doc "needs-pre" [ "pre" ]) (WorkState.Ready at); c (doc "stale" []) (WorkState.Ready at); c (doc "dear" []) (WorkState.Ready at) ]
                    WipLimit = 5
                    Budget = Some { Remaining = 100L; Estimate = fun i -> if i.Id.Value = "dear" then 500L else 1L }
                    Stale = Map.ofList [ WorkItemId "stale", [ "Quote Form changed" ] ] }
          equal [] plan.Pull "nothing"
          let why id = plan.Deferred |> List.find (fun (i, _) -> i.Value = id) |> snd
          equal (WaitsOnDependency [ WorkItemId "pre" ]) (why "needs-pre") "dependency"
          equal (NeedsRecheck [ "Quote Form changed" ]) (why "stale") "recheck"
          equal (OverBudget(500L, 100L)) (why "dear") "budget"
      "scheduler: a done dependency is satisfied, and budget is spent as items are pulled", fun () ->
          let ev = match Evidence.create docItem { facts with Ticks = [ "checklist:audience named" ]; Commit = None; OnMain = None; GateRuns = [] } with Ok e -> e | Error es -> failwithf "%A" es
          let c it st = { Item = it; State = st; Priority = 0; ReadyAt = System.DateTimeOffset.UnixEpoch }
          let doc id deps = created { docDraft with Id = id; Target = Some("document", id); DependsOn = deps }
          let plan =
              Scheduler.plan
                  { Candidates = [ c (doc "pre" []) (WorkState.Done ev); c (doc "a" [ "pre" ]) (WorkState.Ready at); c (doc "b" [ "pre" ]) (WorkState.Ready at) ]
                    WipLimit = 5
                    Budget = Some { Remaining = 15L; Estimate = fun _ -> 10L }
                    Stale = Map.empty }
          equal 1 plan.Pull.Length "the second does not fit what is left"
          check (plan.Deferred |> List.exists (function _, OverBudget(10L, 5L) -> true | _ -> false)) $"%A{plan.Deferred}"
      "recheck: only the item's own elements count, and what moved is said", fun () ->
          let el id code risk = { Id = ElementId id; Name = id; CodeAreas = [ p code ]; Risk = risk; InScope = true }
          let frozen = [ el "UI" "src/UI/**" Low; el "Api" "src/Api/**" Low ]
          let item = [ ElementId "UI" ]
          equal [] (Recheck.reasons item frozen frozen) "same"
          equal [] (Recheck.reasons item frozen [ el "UI" "src/UI/**" Low; el "Api" "src/Api2/**" High ]) "other element moved"
          check (Recheck.reasons item frozen [ el "UI" "src/UI/**" High; el "Api" "src/Api/**" Low ] |> List.contains (RiskChanged(ElementId "UI", Low, High))) "risk"
          check (Recheck.reasons item frozen [ el "UI" "src/Web/**" Low ] |> List.exists (function CodeAreasChanged(e, _, _) -> e.Value = "UI" | _ -> false)) "code"
          check (Recheck.reasons item frozen [ el "Api" "src/Api/**" Low ] |> List.contains (ElementGone(ElementId "UI"))) "gone"
          check (Recheck.describe (ElementGone(ElementId "UI")) <> "") "described" ]
