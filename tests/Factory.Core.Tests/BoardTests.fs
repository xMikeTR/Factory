module Factory.Core.Tests.BoardTests

open Factory.Core
open Factory.Core.Tests.Support

let private item id deps writes =
    match
        WorkItem.create
            { Id = id; Requirement = Some "REQ-1"; Elements = [ id ]; Repo = None; Target = None
              DependsOn = deps; Writes = writes; DoneWhen = [ "gate", "dotnet test" ] }
    with
    | Ok i -> i
    | Error es -> failwithf "%A" es

let private a = item "W-A" [] [ "src/A/**" ]
let private b = item "W-B" [ "W-A" ] [ "src/B/**" ]
let private c = item "W-C" [] [ "src/C/**" ]
let private items = [ a; b; c ]
let private frozen id ts = ts, ItemFrozen(id, "h-" + id, "r1")
let private onMain (_: string) = Some true

let private plan facts =
    let input =
        { Candidates = Board.candidates items [] facts onMain
          WipLimit = Board.wipLimit facts 2
          Budget = None
          Stale = Board.stale facts }
    Scheduler.plan input

let tests: (string * (unit -> unit)) list =
    [ "board: frozen items are Ready and pulled oldest first within the WIP limit", fun () ->
          let p = plan [ frozen "W-C" 2L; frozen "W-A" 1L ]
          equal [ "W-A"; "W-C" ] (p.Pull |> List.map (fun i -> i.Id.Value)) "pull"
      "board: an unfrozen item is not a candidate to pull", fun () ->
          equal [] (plan [] ).Pull "nothing"
      "board: a dependency is satisfied only by a done item with evidence on main", fun () ->
          let started = [ frozen "W-A" 1L; frozen "W-B" 1L; 2L, ItemPulled "W-A" ]
          let finishedWith dirty = started @ [ 3L, ItemGate("W-A", "dotnet test", true, Some "abc1234", dirty); 4L, ItemDone("W-A", Some "abc1234", [ "gate:dotnet test@abc1234" ]) ]
          let finished = finishedWith false
          check ((plan started).Deferred |> List.exists (fun (id, d) -> id.Value = "W-B" && d = WaitsOnDependency [ WorkItemId "W-A" ])) "b waits while a is active"
          equal [ "W-B" ] ((plan finished).Pull |> List.map (fun i -> i.Id.Value)) "b after a is done"
          check ((plan (finishedWith true)).Deferred |> List.exists (fun (id, _) -> id.Value = "W-B")) "a gate that passed on a dirty tree is not evidence"
      "board: a checklist item is Done only when its tick is named in the item_done evidence", fun () ->
          let reviewed =
              match
                  WorkItem.create
                      { Id = "W-R"; Requirement = None; Elements = []; Repo = None; Target = Some("document", "treaty summary")
                        DependsOn = []; Writes = []; DoneWhen = [ "checklist", "numbers match the treaty" ] }
              with
              | Ok i -> i
              | Error es -> failwithf "%A" es
          let state evidence =
              let facts = [ 1L, ItemFrozen("W-R", "h-R", "r1"); 2L, ItemPulled "W-R"; 3L, ItemDone("W-R", None, evidence) ]
              (Board.candidates [ reviewed ] [] facts onMain |> List.exactlyOne).State
          check (match state [] with WorkState.Done _ -> false | _ -> true) "no tick"
          check (match state [ "checklist:numbers match the treaty" ] with WorkState.Done _ -> true | _ -> false) "ticked"
      "board: a parity item is Done only with a passing parity_result at the done commit, recorded before item_done", fun () ->
          let p =
              match
                  WorkItem.create
                      { Id = "W-P"; Requirement = Some "REQ-1"; Elements = [ "W-P" ]; Repo = None; Target = None
                        DependsOn = []; Writes = []; DoneWhen = [ "gate", "dotnet test"; "parity", "all" ] }
              with
              | Ok i -> i
              | Error es -> failwithf "%A" es
          let state parity =
              let facts =
                  [ 1L, ItemFrozen("W-P", "h-P", "r1"); 2L, ItemPulled "W-P"; 3L, ItemGate("W-P", "dotnet test", true, Some "abc1234", false) ]
                  @ parity @ [ 5L, ItemDone("W-P", Some "abc1234", []) ]
              (Board.candidates [ p ] [ "quote-list" ] (List.sortBy fst facts) onMain |> List.exactlyOne).State
          let isDone = function WorkState.Done _ -> true | _ -> false
          check (isDone (state [ 4L, ParityResult("quote-list", "abc1234", ParityPass) ])) "passed"
          check (not (isDone (state []))) "never recorded"
          check (not (isDone (state [ 4L, ParityResult("quote-list", "abc1234", ParityCouldNotRun) ]))) "could not run"
          check (not (isDone (state [ 6L, ParityResult("quote-list", "abc1234", ParityPass) ]))) "recorded after the done row"
      "board: a stale re-check defers the item with its reasons until it is frozen again", fun () ->
          let facts = [ frozen "W-C" 1L; 2L, ItemRecheck("W-C", true, [ "Pricing moved" ]) ]
          match (plan facts).Deferred with
          | [ id, NeedsRecheck [ "Pricing moved" ] ] -> equal "W-C" id.Value "item"
          | other -> failwithf "%A" other
          equal [ "W-C" ] ((plan (facts @ [ frozen "W-C" 3L ])).Pull |> List.map (fun i -> i.Id.Value)) "re-frozen"
      "board: the WIP limit follows the last wip_limit_changed", fun () ->
          equal 1 (Board.wipLimit [ 1L, WipLimitChanged 5; 2L, WipLimitChanged 1 ] 3) "limit"
          equal 3 (Board.wipLimit [] 3) "default" ]
