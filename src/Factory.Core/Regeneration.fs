namespace Factory.Core

/// What a re-frozen handover changes for an existing plan. Changed: decisions added, removed, or
/// with a different statement, binds or checks (in id order). Rerun: the plan's tasks that cover a
/// changed decision or touch an element one binds, before or after the change (in plan order).
type Regeneration =
    { Changed: DecisionId list
      Rerun: TaskId list }

[<RequireQualifiedAccess>]
module Regeneration =

    /// Only decisions drive a re-run: an element whose code areas or risk changed, or a task that
    /// depends on a re-run task, is not re-run here. Integration verify still runs the whole suite.
    let between (before: Handover) (after: Handover) (plan: Plan) : Regeneration =
        let byId (h: Handover) = h.Decisions |> List.map (fun d -> d.Id, d) |> Map.ofList
        let b, a = byId before, byId after
        let changed =
            Set.union (Set.ofSeq b.Keys) (Set.ofSeq a.Keys)
            |> Set.filter (fun id -> Map.tryFind id b <> Map.tryFind id a)
            |> Set.toList
            |> List.sortBy (fun id -> id.Value)
        let bound =
            changed
            |> List.collect (fun d -> [ Map.tryFind d b; Map.tryFind d a ] |> List.choose Operators.id |> List.collect (fun d -> d.Binds))
            |> Set.ofList
        let rerun =
            plan.Tasks
            |> List.filter (fun t ->
                t.Covers |> List.exists (fun c -> List.contains c changed)
                || t.Elements |> List.exists bound.Contains)
            |> List.map (fun t -> t.Id)
        { Changed = changed; Rerun = rerun }
