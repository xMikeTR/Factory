module Factory.Core.Tests.MetricsTests

open Factory.Core
open Factory.Core.Tests.Support

let private item id =
    match
        WorkItem.create
            { Id = id; Requirement = Some "REQ-1"; Elements = [ "Pricing/Form" ]; Repo = None; Target = None
              DependsOn = []; Writes = [ "src/**" ]; DoneWhen = [ "gate", "dotnet test" ] }
    with
    | Ok i -> i
    | Error es -> failwithf "%A" es

let private items = [ item "W-A"; item "W-B" ]
let private metrics facts =
    let d = Digest.compute items facts (fun _ -> Some true) 3 0L
    Metrics.compute items facts d

let private day = 86_400_000L
let private t0 = 1_700_000_000_000L // 2023-11-14, ISO week 46

let tests: (string * (unit -> unit)) list =
    [ "metrics: throughput counts done items per ISO week and target kind, lead time is pull to done", fun () ->
          let m =
              metrics [ t0, ItemPulled "W-A"; t0 + 2L * day, ItemDone("W-A", Some "abc1234", 1) ]
          equal [ ("2023-W46", "repo"), 1 ] m.Throughput "throughput"
          equal (Some 2L) (m.LeadTime |> Option.map (fun t -> t.Median / day)) "lead"
      "metrics: a done claim without evidence is not throughput", fun () ->
          let m = metrics [ t0, ItemPulled "W-A"; t0 + day, ItemDone("W-A", Some "abc1234", 0) ]
          equal [] m.Throughput "none"
      "metrics: human wait closes at the pull; an unanswered wait is open", fun () ->
          let m =
              metrics
                  [ t0, ItemAwaitingHuman("W-A", None)
                    t0 + day, ItemPulled "W-A"
                    t0, ItemAwaitingHuman("W-B", None) ]
          equal (Some day) (m.HumanWait |> Option.map (fun t -> t.Median)) "wait"
          equal 1 m.OpenWaits "open"
      "metrics: deferrals, recheck rate and escapes by origin", fun () ->
          let m =
              metrics
                  [ 1L, ConflictDeferred("W-B", None)
                    2L, ItemRecheck("W-A", true, [ "moved" ])
                    3L, ItemRecheck("W-B", false, [])
                    4L, EscapeRecorded("BUG-1", Some "spec_gap")
                    5L, EscapeRecorded("BUG-2", Some "spec_gap")
                    6L, EscapeRecorded("BUG-3", None) ]
          equal 1 m.ConflictDeferrals "deferrals"
          equal (Some 0.5) m.RecheckRate "recheck"
          equal [ "spec_gap", 2; "unclassified", 1 ] m.EscapesByOrigin "escapes"
      "metrics: no facts, no numbers", fun () ->
          let m = metrics []
          check (m.LeadTime.IsNone && m.HumanWait.IsNone && m.RecheckRate.IsNone) "empty" ]
