module Factory.Core.Tests.DigestTests

open Factory.Core
open Factory.Core.Tests.Support

let private item id elements writes gates deps =
    match
        WorkItem.create
            { Id = id; Requirement = Some "REQ-1"; Elements = elements; Repo = None; Target = None
              DependsOn = deps; Writes = writes; DoneWhen = gates |> List.map (fun g -> "gate", g) }
    with
    | Ok i -> i
    | Error es -> failwithf "%A" es

let a = item "W-A" [ "Pricing/Form" ] [ "src/A/**" ] [ "dotnet test" ] []
let b = item "W-B" [ "Pricing/Form" ] [ "src/B/**" ] [ "dotnet test" ] []
let c = item "W-C" [ "Pricing/Other" ] [ "src/C/**" ] [ "dotnet test" ] [ "W-A" ]
let items = [ a; b; c ]
let onMain (_: string) = Some true
let compute facts = Digest.compute items facts onMain 3 0L

let tests: (string * (unit -> unit)) list =
    [ "digest: an unpulled item waits on its dependency and conflicts with an active item", fun () ->
          let d = compute [ 1L, ItemPulled "W-A" ]
          let texts = d.Waiting |> List.map (fun w -> w.Text)
          check (texts |> List.exists (fun t -> t.StartsWith "W-B conflicts with W-A")) $"%A{texts}"
          check (texts |> List.contains "W-C depends on W-A, which is not done") $"%A{texts}"
      "digest: an open needs-decision is Needs until resolved", fun () ->
          let opened = 1L, NeedsDecisionOpened("ND-1", Some "W-A", None, "Dedupe?")
          equal 1 (compute [ opened ]).Needs.Length "open"
          equal 0 (compute [ opened; 2L, NeedsDecisionResolved "ND-1" ]).Needs.Length "resolved"
      "digest: awaiting-human stands until the item is pulled, gated or done", fun () ->
          let w = 1L, ItemAwaitingHuman("W-A", Some "needs a call")
          equal 1 (compute [ w ]).Needs.Length "waiting"
          equal 0 (compute [ w; 2L, ItemPulled "W-A" ]).Needs.Length "pulled"
      "digest: the last run of a gate decides Failed, and a later pass clears it", fun () ->
          let pulled = 1L, ItemPulled "W-A"
          let fail = 2L, ItemGate("W-A", "dotnet test", false, Some "abcdef123")
          equal [ GateFailed ] ((compute [ pulled; fail ]).Failed |> List.map (fun f -> f.Kind)) "fail"
          equal 0 (compute [ pulled; fail; 3L, ItemGate("W-A", "dotnet test", true, Some "abcdef123") ]).Failed.Length "pass"
      "digest: done off main or without evidence is Failed, never Done", fun () ->
          let finished ev = [ 1L, ItemPulled "W-A"; 2L, ItemDone("W-A", Some "abc1234", ev) ]
          equal 1 (compute (finished [ "gate:dotnet test@abc1234" ])).Done.Length "done"
          equal [ NoEvidence ] ((compute (finished [])).Failed |> List.map (fun f -> f.Kind)) "no evidence"
          let d = Digest.compute items (finished [ "gate:dotnet test@abc1234" ]) (fun _ -> Some false) 3 0L
          equal [ NotOnMain ] (d.Failed |> List.map (fun f -> f.Kind)) "not on main"
          equal 0 d.Done.Length "not done"
      "digest: an acceptance failure stands until the item passes a gate", fun () ->
          let f = 2L, AcceptanceFailed("W-A", "AC-1", "Pricing/Form", "abcdef123")
          equal [ AcceptanceFailure ] ((compute [ 1L, ItemPulled "W-A"; f ]).Failed |> List.map (fun x -> x.Kind)) "stands"
          equal 0 (compute [ 1L, ItemPulled "W-A"; f; 3L, ItemGate("W-A", "dotnet test", true, None) ]).Failed.Length "cleared"
      "digest: only Needs and Failed change the notification signature", fun () ->
          let base' = [ 1L, ItemPulled "W-A" ]
          let withDone = base' @ [ 2L, ItemDone("W-A", Some "abc1234", [ "gate:dotnet test@abc1234" ]) ]
          equal (Digest.signature (compute base')) (Digest.signature (compute withDone)) "done does not ping"
          let withNeed = base' @ [ 3L, NeedsDecisionOpened("ND-2", None, None, "q") ]
          check (Digest.signature (compute base') <> Digest.signature (compute withNeed)) "need pings"
      "digest: the latest wip_limit_changed wins and a full limit is reported", fun () ->
          let d = compute [ 1L, WipLimitChanged 1; 2L, ItemPulled "W-A" ]
          equal 1 d.WipLimit "limit"
          check (d.Waiting |> List.exists (fun w -> w.Item.IsNone && w.Text.Contains "WIP limit of 1")) "wip row"
      "digest: an exceeded budget is Waiting until within or a later pull, never Needs or Failed", fun () ->
          let over = 2L, ItemBudgetReading("W-A", "exceeded", Some 200000L, Some 260000L, None)
          let d = compute [ 1L, ItemPulled "W-A"; over ]
          check (d.Waiting |> List.exists (fun w -> w.Text = "W-A budget exceeded (260000 of 200000 tokens)")) $"%A{d.Waiting}"
          equal 0 d.Needs.Length "needs"
          equal 0 d.Failed.Length "failed"
          let within = 3L, ItemBudgetReading("W-A", "within", Some 400000L, Some 260000L, None)
          check ((compute [ over; within ]).Waiting |> List.forall (fun w -> not (w.Text.Contains "budget"))) "within clears"
          check ((compute [ over; 3L, ItemPulled "W-A" ]).Waiting |> List.forall (fun w -> not (w.Text.Contains "budget"))) "pull clears"
          equal (Digest.signature (compute [ 1L, ItemPulled "W-A" ])) (Digest.signature (compute [ 1L, ItemPulled "W-A"; over ])) "no ping"
      "digest: an alert is announced once and clearing it never pings", fun () ->
          let need = 1L, NeedsDecisionOpened("ND-1", Some "W-A", None, "Dedupe?")
          let first = Digest.newAlerts Set.empty (compute [ need ])
          equal 1 first.Length "new"
          let seen = Set.ofList first
          equal [] (Digest.newAlerts seen (compute [ need ])) "already announced"
          equal [] (Digest.newAlerts seen (compute [ need; 2L, NeedsDecisionResolved "ND-1" ])) "cleared" ]
