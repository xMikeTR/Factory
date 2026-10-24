namespace Factory.Core

open System

/// The registry's ledger folded into what the scheduler needs: each item's state, when it became Ready,
/// which frozen items went stale, and the WIP limit. Pure, so the same facts always give the same plan.
[<RequireQualifiedAccess>]
module Board =
    /// Backlog until a handover freezes the item; Done only through `Evidence.create` from the recorded gate runs
    /// (a done claim that fails it leaves the item Active, so it stays in flight and out of the next pull).
    /// A person's ticks are the `checklist:` / `review:` entries of the item_done row's evidence (designc records them there),
    /// so a checklist or review item reaches Done only with its tick named on that row. Parity results recorded before
    /// the item_done row count, for any item; `parityScenarios` (the registry's ids) is what "parity: all" means.
    let candidates (items: WorkItem list) (parityScenarios: string list) (facts: (int64 * LedgerFact) list) (onMain: string -> bool option) : Candidate list =
        let ev = facts |> List.sortBy fst
        items
        |> List.map (fun i ->
            let id = i.Id.Value
            let step (state, readyAt, (runs: GateRun list, parity: ParityRun list)) (ts: int64, f: LedgerFact) =
                let apply e = WorkState.apply state e |> Result.defaultValue state
                match f with
                | ItemFrozen(x, h, rev) when x = id ->
                    let from = match state with WorkState.Ready _ -> WorkState.Backlog | s -> s
                    match WorkState.apply from (WorkEvent.Frozen { Handover = h; DesignVersion = rev }) with
                    | Ok s -> s, DateTimeOffset.FromUnixTimeMilliseconds ts, (runs, parity)
                    | Error _ -> state, readyAt, (runs, parity)
                | ItemPulled x when x = id -> apply (WorkEvent.Pulled(RunId id)), readyAt, (runs, parity)
                | ItemAwaitingHuman(x, why) when x = id -> apply (WorkEvent.AwaitedHuman(defaultArg why "")), readyAt, (runs, parity)
                | ItemGate(x, g, passed, commit, dirty) when x = id -> state, readyAt, (runs @ [ { Gate = g; Commit = commit; Passed = passed; Dirty = dirty } ], parity)
                | ParityResult(s, commit, outcome) -> state, readyAt, (runs, parity @ [ { Scenario = s; Commit = commit; Outcome = outcome } ])
                | ItemDone(x, commit, evidence) when x = id ->
                    let ticks = evidence |> List.filter (fun e -> e.StartsWith "checklist:" || e.StartsWith "review:")
                    let facts' =
                        { Commit = commit; OnMain = commit |> Option.bind onMain; GateRuns = runs; Ticks = ticks
                          ParityRuns = parity; ParityScenarios = parityScenarios }
                    match Evidence.create i facts' with
                    | Ok evidence -> apply (WorkEvent.Finished evidence), readyAt, (runs, parity)
                    | Error _ -> state, readyAt, (runs, parity)
                | _ -> state, readyAt, (runs, parity)
            let state, readyAt, _ = ev |> List.fold step (WorkState.Backlog, DateTimeOffset.UnixEpoch, ([], []))
            { Item = i; State = state; Priority = 0; ReadyAt = readyAt })

    /// The latest re-check of each item decides: stale with its reasons, or clear. A re-freeze after it
    /// supersedes it (the item was frozen again against the moved design).
    let stale (facts: (int64 * LedgerFact) list) : Map<WorkItemId, string list> =
        let ev = facts |> List.sortBy fst
        ev
        |> List.fold
            (fun m (_, f) ->
                match f with
                | ItemRecheck(i, true, reasons) -> Map.add (WorkItemId i) (if reasons.IsEmpty then [ "the design moved" ] else reasons) m
                | ItemRecheck(i, false, _) -> Map.remove (WorkItemId i) m
                | ItemFrozen(i, _, _) -> Map.remove (WorkItemId i) m
                | _ -> m)
            Map.empty

    let wipLimit (facts: (int64 * LedgerFact) list) (defaultWip: int) : int =
        facts |> List.sortBy fst |> List.fold (fun w (_, f) -> match f with WipLimitChanged n -> n | _ -> w) defaultWip
