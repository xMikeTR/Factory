namespace Factory.Core

open System

type ConflictReason =
    | SharedWrites of PathPattern * PathPattern
    | SharedElement of ElementId
    /// "<kind> <value>": two items that target the same document, ticket or question.
    | SameTarget of string

type Conflict = { With: WorkItemId; Reason: ConflictReason }

/// Why a Ready item was not pulled. It stays Ready; nothing here changes its state.
type Deferral =
    | WaitsOnDependency of WorkItemId list
    | ConflictsWith of Conflict list
    | OverBudget of needed: int64 * remaining: int64
    | WipFull of inFlight: int * limit: int
    /// The design moved under the frozen item; it must be re-frozen before it can be pulled.
    | NeedsRecheck of reasons: string list

type Candidate =
    { Item: WorkItem
      State: WorkState
      /// Higher is pulled first.
      Priority: int
      /// When it became Ready; older is pulled first among equal priorities.
      ReadyAt: DateTimeOffset }

/// Tokens still to spend, and what an item is expected to cost (S4 history; a fixed default until there is any).
type Budget =
    { Remaining: int64
      Estimate: WorkItem -> int64 }

type SchedulerInput =
    { Candidates: Candidate list
      WipLimit: int
      Budget: Budget option
      /// Frozen items whose design moved (see Recheck), with the reasons.
      Stale: Map<WorkItemId, string list> }

type PullPlan =
    { Pull: WorkItem list
      Deferred: (WorkItemId * Deferral) list }

[<RequireQualifiedAccess>]
module Scheduler =
    let private targetKey =
        function
        | Document v -> Some $"document {v}"
        | Jira v -> Some $"jira {v}"
        | Investigation v -> Some $"investigation {v}"
        | Repo _ -> None

    /// Why two items must not run at the same time, if they must not (designc's Work.conflicts, one level up
    /// from the plan's own path-overlap rule).
    let conflict (a: WorkItem) (b: WorkItem) : ConflictReason option =
        let writes = [ for x in a.Writes do for y in b.Writes do if PathPattern.mayOverlap x y then SharedWrites(x, y) ]
        match writes with
        | w :: _ -> Some w
        | [] ->
            match WorkItem.elements a |> List.tryFind (fun e -> List.contains e (WorkItem.elements b)) with
            | Some e -> Some(SharedElement e)
            | None ->
                match targetKey a.Target, targetKey b.Target with
                | Some x, Some y when x = y -> Some(SameTarget x)
                | _ -> None

    /// Fill the free WIP slots. Order: priority, then age. Per item the first reason that applies wins:
    /// stale freeze, dependency not done, conflict with an in-flight or just-pulled item, budget, no slot.
    let plan (input: SchedulerInput) : PullPlan =
        let inFlight = input.Candidates |> List.filter (fun c -> WorkState.inFlight c.State)
        let doneIds = input.Candidates |> List.filter (fun c -> match c.State with WorkState.Done _ -> true | _ -> false) |> List.map (fun c -> c.Item.Id) |> Set.ofList
        let ready =
            input.Candidates
            |> List.filter (fun c -> match c.State with WorkState.Ready _ -> true | _ -> false)
            |> List.sortBy (fun c -> -c.Priority, c.ReadyAt, c.Item.Id.Value)

        let step (pulled: WorkItem list, deferred, remaining: int64 option) (c: Candidate) =
            let holders = (inFlight |> List.map (fun x -> x.Item)) @ pulled
            let clashes = holders |> List.choose (fun h -> conflict c.Item h |> Option.map (fun r -> { With = h.Id; Reason = r }))
            let missing = c.Item.DependsOn |> List.filter (fun d -> not (doneIds.Contains d))
            let cost = input.Budget |> Option.map (fun b -> b.Estimate c.Item)
            let verdict =
                match Map.tryFind c.Item.Id input.Stale with
                | Some reasons when not reasons.IsEmpty -> Some(NeedsRecheck reasons)
                | _ when not missing.IsEmpty -> Some(WaitsOnDependency missing)
                | _ when not clashes.IsEmpty -> Some(ConflictsWith clashes)
                | _ ->
                    match cost, remaining with
                    | Some need, Some left when need > left -> Some(OverBudget(need, left))
                    | _ when holders.Length >= input.WipLimit -> Some(WipFull(inFlight.Length, input.WipLimit))
                    | _ -> None
            match verdict with
            | Some d -> pulled, deferred @ [ c.Item.Id, d ], remaining
            | None -> pulled @ [ c.Item ], deferred, (match remaining, cost with Some r, Some n -> Some(r - n) | r, _ -> r)

        let pulled, deferred, _ = ready |> List.fold step ([], [], input.Budget |> Option.map (fun b -> b.Remaining))
        { Pull = pulled; Deferred = deferred }

/// Has the design moved under a frozen item? S2 does not parse the DSL, so this compares the element
/// specs (code areas, risk, presence) of the handover the item was frozen into with the current ones.
/// Finer drift (fingerprints, bound decisions) is designc's `workRecheck`, which writes `item_recheck`.
type RecheckReason =
    | ElementGone of ElementId
    | CodeAreasChanged of ElementId * before: PathPattern list * after: PathPattern list
    | RiskChanged of ElementId * before: RiskLevel * after: RiskLevel

[<RequireQualifiedAccess>]
module Recheck =
    let reasons (itemElements: ElementId list) (frozen: ElementSpec list) (current: ElementSpec list) : RecheckReason list =
        [ for id in itemElements do
              match frozen |> List.tryFind (fun e -> e.Id = id), current |> List.tryFind (fun e -> e.Id = id) with
              | Some _, None -> ElementGone id
              | Some f, Some c ->
                  if f.CodeAreas <> c.CodeAreas then CodeAreasChanged(id, f.CodeAreas, c.CodeAreas)
                  if f.Risk <> c.Risk then RiskChanged(id, f.Risk, c.Risk)
              | None, _ -> () ]

    let describe =
        function
        | ElementGone e -> $"{e.Value} is no longer in the design."
        | CodeAreasChanged(e, b, a) ->
            let show (ps: PathPattern list) = ps |> List.map (fun p -> p.Value) |> String.concat ", "
            $"{e.Value} code areas changed from [{show b}] to [{show a}]."
        | RiskChanged(e, b, a) -> $"{e.Value} risk changed from {b} to {a}."
