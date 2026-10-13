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
        { Candidates = Board.candidates items facts onMain
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
          let finished = started @ [ 3L, ItemGate("W-A", "dotnet test", true, Some "abc1234"); 4L, ItemDone("W-A", Some "abc1234", 1) ]
          check ((plan started).Deferred |> List.exists (fun (id, d) -> id.Value = "W-B" && d = WaitsOnDependency [ WorkItemId "W-A" ])) "b waits while a is active"
          equal [ "W-B" ] ((plan finished).Pull |> List.map (fun i -> i.Id.Value)) "b after a is done"
      "board: a stale re-check defers the item with its reasons until it is frozen again", fun () ->
          let facts = [ frozen "W-C" 1L; 2L, ItemRecheck("W-C", true, [ "Pricing moved" ]) ]
          match (plan facts).Deferred with
          | [ id, NeedsRecheck [ "Pricing moved" ] ] -> equal "W-C" id.Value "item"
          | other -> failwithf "%A" other
          equal [ "W-C" ] ((plan (facts @ [ frozen "W-C" 3L ])).Pull |> List.map (fun i -> i.Id.Value)) "re-frozen"
      "board: the WIP limit follows the last wip_limit_changed", fun () ->
          equal 1 (Board.wipLimit [ 1L, WipLimitChanged 5; 2L, WipLimitChanged 1 ] 3) "limit"
          equal 3 (Board.wipLimit [] 3) "default" ]
