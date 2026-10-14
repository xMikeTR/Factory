namespace Factory.Core

/// One ledger row that matters to the work digest, read from the registry (Harness/Registry/WorkRegistryReader).
/// Facts arrive oldest first with their epoch-ms timestamp.
type LedgerFact =
    | NeedsDecisionOpened of id: string * task: string option * element: string option * question: string
    | NeedsDecisionResolved of id: string
    | ItemAwaitingHuman of item: string * reason: string option
    | ItemPulled of item: string
    | ItemGate of item: string * gate: string * passed: bool * commit: string option
    /// `evidence` is the row's evidence entries: `gate:<cmd>@<commit>`, and the person's ticks `checklist:<text>` / `review:<name>`.
    | ItemDone of item: string * commit: string option * evidence: string list
    | AcceptanceFailed of task: string * ac: string * element: string * commit: string
    | ConflictDeferred of item: string * reason: string option
    | WipLimitChanged of limit: int
    /// A design-version re-check of a frozen item; stale = its elements moved since the freeze.
    | ItemRecheck of item: string * stale: bool * reasons: string list
    /// A handover that names the item: the item was frozen into it at this design revision.
    | ItemFrozen of item: string * handover: string * revision: string
    /// A pipeline's token budget reading for an item (`item_budget`): status is within | exceeded | waiting.
    | ItemBudgetReading of item: string * status: string * limitTokens: int64 option * spentTokens: int64 option * reason: string option
    /// A bug closed at intake, with designc's origin (spec_gap | design_error | translation_error | gate_gap).
    | EscapeRecorded of id: string * origin: string option

type DigestState =
    | InBacklog
    | InProgress
    | Finished

type DigestEntry =
    { Item: string option
      Text: string
      Ts: int64 }

type DigestFailure =
    | GateFailed
    | NotOnMain
    | NoEvidence
    | AcceptanceFailure

type DigestFailed =
    { Item: string
      Kind: DigestFailure
      Text: string
      Ts: int64 }

type Digest =
    { Needs: DigestEntry list
      Failed: DigestFailed list
      Done: (string * string option * int64) list
      Waiting: DigestEntry list
      WipLimit: int
      Active: int }

/// The four buckets a person looks at, computed from the ledger and what could be checked in git
/// (designc's Work.digest). An agent's claim is never evidence: a done item whose commit is not on
/// main, or that names no evidence, is Failed rather than Done. Pure.
[<RequireQualifiedAccess>]
module Digest =
    let private c7 (c: string) = if c.Length > 7 then c.Substring(0, 7) else c

    let private describeConflict (c: ConflictReason) =
        match c with
        | SharedWrites(a, b) -> $"both may write {a.Value} and {b.Value}"
        | SharedElement e -> $"both change element {e.Value}"
        | SameTarget t -> $"both target {t}"

    /// backlog until pulled, in progress until done. The ledger decides.
    let states (items: WorkItem list) (facts: (int64 * LedgerFact) list) =
        items
        |> List.map (fun i ->
            let id = i.Id.Value
            let s =
                facts
                |> List.fold
                    (fun s (_, f) ->
                        match f with
                        | ItemPulled x when x = id -> InProgress
                        | ItemDone(x, _, _) when x = id -> Finished
                        | _ -> s)
                    InBacklog
            i.Id, s)
        |> Map.ofList

    /// `onMain` maps a commit to Some true / Some false, or None when it could not be checked (then it is not judged).
    let compute (items: WorkItem list) (facts: (int64 * LedgerFact) list) (onMain: string -> bool option) (defaultWip: int) (lastLook: int64) : Digest =
        let ev = facts |> List.sortBy fst
        let st = states items ev
        let stateOf (id: string) = st |> Map.tryFind (WorkItemId id) |> Option.defaultValue InBacklog
        let after (pred: LedgerFact -> bool) ts = ev |> List.exists (fun (t, f) -> t > ts && pred f)
        let resolved = ev |> List.choose (function _, NeedsDecisionResolved id -> Some id | _ -> None) |> Set.ofList

        let needs =
            ev
            |> List.choose (fun (ts, f) ->
                match f with
                | NeedsDecisionOpened(id, task, element, q) when not (resolved.Contains id) ->
                    Some { Item = task; Text = q; Ts = ts }
                | ItemAwaitingHuman(item, reason) when not (after (function
                                                                  | ItemPulled i | ItemDone(i, _, _) -> i = item
                                                                  | ItemGate(i, _, _, _) -> i = item
                                                                  | _ -> false) ts) ->
                    let why = reason |> Option.map (fun r -> ": " + r) |> Option.defaultValue ""
                    Some { Item = Some item; Text = $"{item} is waiting for a person{why}"; Ts = ts }
                | _ -> None)

        let gateFailures =
            items
            |> List.filter (fun i -> stateOf i.Id.Value = InProgress)
            |> List.collect (fun i ->
                i.DoneWhen.ToList
                |> List.choose (function Gate g -> Some g | _ -> None)
                |> List.choose (fun g ->
                    ev
                    |> List.filter (fun (_, f) -> match f with ItemGate(x, y, _, _) -> x = i.Id.Value && y = g | _ -> false)
                    |> List.tryLast
                    |> Option.bind (fun (ts, f) ->
                        match f with
                        | ItemGate(_, _, false, commit) ->
                            let at = commit |> Option.map (fun c -> " at " + c7 c) |> Option.defaultValue ""
                            Some { Item = i.Id.Value; Kind = GateFailed; Text = $"{i.Id.Value}: gate \"{g}\" failed{at}"; Ts = ts }
                        | _ -> None)))

        let doneFailures =
            items
            |> List.filter (fun i -> stateOf i.Id.Value = Finished)
            |> List.choose (fun i ->
                ev
                |> List.filter (fun (_, f) -> match f with ItemDone(x, _, _) -> x = i.Id.Value | _ -> false)
                |> List.tryLast
                |> Option.bind (fun (ts, f) ->
                    match f with
                    | ItemDone(_, Some c, _) when onMain c = Some false ->
                        Some { Item = i.Id.Value; Kind = NotOnMain; Text = $"{i.Id.Value}: Claimed merged, not on main ({c7 c})"; Ts = ts }
                    | ItemDone(_, _, []) -> Some { Item = i.Id.Value; Kind = NoEvidence; Text = $"{i.Id.Value}: Claimed done without evidence"; Ts = ts }
                    | _ -> None))

        // A pipeline's UI acceptance failure stands until the same item later passes a gate or is done.
        let acceptance =
            ev
            |> List.choose (fun (ts, f) -> match f with AcceptanceFailed(t, ac, el, c) -> Some((t, ac), (ts, el, c)) | _ -> None)
            |> List.fold (fun m (k, v) -> Map.add k v m) Map.empty
            |> Map.toList
            |> List.choose (fun ((task, ac), (ts, el, c)) ->
                let cleared = after (function ItemDone(i, _, _) -> i = task | ItemGate(i, _, true, _) -> i = task | _ -> false) ts
                if cleared then None
                else Some { Item = task; Kind = AcceptanceFailure; Text = $"{task}: acceptance {ac} failed on {el} at {c7 c}"; Ts = ts })

        let failed = gateFailures @ doneFailures @ acceptance
        let failedItems = failed |> List.map (fun f -> f.Item) |> Set.ofList

        let doneNow =
            ev
            |> List.choose (fun (ts, f) ->
                match f with
                | ItemDone(i, c, _) when ts > lastLook && stateOf i = Finished && not (failedItems.Contains i) -> Some(i, c, ts)
                | _ -> None)

        let limit = ev |> List.choose (function _, WipLimitChanged l -> Some l | _ -> None) |> List.tryLast |> Option.defaultValue defaultWip
        let active = items |> List.filter (fun i -> stateOf i.Id.Value = InProgress)
        let backlog = items |> List.filter (fun i -> stateOf i.Id.Value = InBacklog)

        let waiting =
            [ for it in backlog do
                  let id = it.Id.Value
                  for a in active do
                      match Scheduler.conflict it a with
                      | Some r -> yield { Item = Some id; Text = $"{id} conflicts with {a.Id.Value}: {describeConflict r}"; Ts = 0L }
                      | None -> ()
                  for d in it.DependsOn |> List.filter (fun d -> Map.tryFind d st <> Some Finished) do
                      yield { Item = Some id; Text = $"{id} depends on {d.Value}, which is not done"; Ts = 0L }
                  let deferred =
                      ev
                      |> List.filter (fun (_, f) -> match f with ConflictDeferred(x, _) -> x = id | _ -> false)
                      |> List.tryLast
                  match deferred with
                  | Some(ts, ConflictDeferred(_, reason)) when not (after (function ItemPulled i -> i = id | _ -> false) ts) ->
                      let why = reason |> Option.map (fun r -> ": " + r) |> Option.defaultValue ""
                      yield { Item = Some id; Text = $"{id} was deferred{why}"; Ts = ts }
                  | _ -> ()
              // designc's rule: the latest budget row per item decides; `within` or a later pull clears it.
              // Never Needs or Failed.
              let latestBudget =
                  ev
                  |> List.choose (fun (ts, f) -> match f with ItemBudgetReading(i, s, l, sp, r) -> Some(i, (ts, s, l, sp, r)) | _ -> None)
                  |> List.fold (fun m (i, v) -> Map.add i v m) Map.empty
              for KeyValue(i, (ts, status, limitT, spentT, reason)) in latestBudget do
                  if status <> "within" && not (after (function ItemPulled x -> x = i | _ -> false) ts) then
                      let amount =
                          match spentT, limitT with
                          | Some s, Some l -> $" ({s} of {l} tokens)"
                          | _ -> ""
                      let why = reason |> Option.map (fun r -> ": " + r) |> Option.defaultValue ""
                      yield { Item = Some i; Text = $"{i} budget {status}{amount}{why}"; Ts = ts }
              if not backlog.IsEmpty && active.Length >= limit then
                  let noun = if backlog.Length = 1 then "item waits" else "items wait"
                  yield { Item = None; Text = $"{active.Length} active against a WIP limit of {limit}: {backlog.Length} {noun}"; Ts = 0L } ]

        { Needs = needs; Failed = failed; Done = doneNow; Waiting = waiting; WipLimit = limit; Active = active.Length }

    /// What a desktop notification may react to: only Needs and Failed, so a new Done or Waiting row never pings.
    let signature (d: Digest) =
        let n = d.Needs |> List.map (fun e -> e.Item, e.Text, e.Ts)
        let f = d.Failed |> List.map (fun e -> e.Item, e.Kind, e.Ts)
        sprintf "%A|%A" n f

    /// One stable key per Needs and Failed row; Done and Waiting never alert.
    let alertKeys (d: Digest) : string list =
        [ for n in d.Needs -> "needs|" + defaultArg n.Item "" + "|" + string n.Ts + "|" + n.Text
          for f in d.Failed -> $"failed|{f.Item}|{f.Kind}|{f.Ts}" ]

    /// The alerts a notification may carry: rows not in `seen`. A row that clears never pings,
    /// and a row that was already announced never pings again.
    let newAlerts (seen: Set<string>) (d: Digest) : string list =
        alertKeys d |> List.filter (fun k -> not (Set.contains k seen))
