module Factory.Core.Tests.WorkTests

open Factory.Core
open Factory.Core.Tests.Support

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

let facts = { Commit = Some "abc1234"; OnMain = Some true; GateRuns = [ { Gate = "dotnet test"; Commit = Some "abc1234"; Passed = true; Dirty = false } ]; Ticks = [] }

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
          check (Result.isError (WorkState.apply (WorkState.Done ev) (WorkEvent.Rechecked "x"))) "done" ]
