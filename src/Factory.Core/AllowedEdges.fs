namespace Factory.Core

/// What kind of code edge an adapter found: a project reference, a type used across assemblies, a TS import.
type EdgeKind =
    | ProjectReference
    | AssemblyUse
    | Import

/// A code edge between two design elements that the design does not allow.
type AllowViolation =
    { Kind: EdgeKind
      Edge: CodeEdge
      FromElement: ElementId
      ToElement: ElementId }

/// Architecture fitness from the design: code in one element's area may depend on another element's code only if the
/// design allows it (designc dependencies.json, or the plain relationships of an older handover). Directed.
[<RequireQualifiedAccess>]
module AllowedEdges =

    /// Who each element may depend on: dependencies.json when the handover has one, else the model's relationships as they are.
    let allows (h: Handover) : Map<ElementId, Set<ElementId>> =
        match h.Dependencies with
        | Some deps -> deps |> List.map (fun d -> d.Element, Set.ofList d.Allows) |> Map.ofList
        | None ->
            h.Relationships
            |> List.groupBy (fun r -> r.From)
            |> List.map (fun (from, rs) -> from, rs |> List.map (fun r -> r.To) |> Set.ofList)
            |> Map.ofList

    /// The repo a task works in: the single repo of its elements; None when they name none or more than one.
    let taskRepo (elements: ElementSpec list) (task: PlannedTask) : string option =
        elements
        |> List.filter (fun e -> List.contains e.Id task.Elements)
        |> List.choose (fun e -> e.Repo)
        |> List.distinct
        |> function [ r ] -> Some r | _ -> None

    /// The first element whose code area matches the path. In a run of the task's repo, only that repo's elements are candidates.
    let owner (elements: ElementSpec list) (repo: string option) (path: string) : ElementSpec option =
        elements
        |> List.filter (fun e -> match repo with Some r -> e.Repo = Some r | None -> true)
        |> List.tryFind (fun e -> e.CodeAreas |> List.exists (fun a -> PathPattern.matches a path))

    /// Edges that cross between two elements the design does not connect, in that direction. Code no element owns is not judged.
    let violations (h: Handover) (repo: string option) (kind: EdgeKind) (edges: CodeEdge list) : AllowViolation list =
        let allowed = allows h
        edges
        |> List.choose (fun edge ->
            match owner h.Elements repo edge.Source, owner h.Elements repo edge.Target with
            | Some a, Some b when a.Id <> b.Id && not ((allowed |> Map.tryFind a.Id |> Option.defaultValue Set.empty).Contains b.Id) ->
                Some { Kind = kind; Edge = edge; FromElement = a.Id; ToElement = b.Id }
            | _ -> None)

    let private pairs (vs: AllowViolation list) = vs |> List.map (fun v -> v.FromElement, v.ToElement)

    /// The ratchet: an element pair the baseline already crossed is debt, not the task's doing.
    let newSince (baseline: AllowViolation list) (current: AllowViolation list) : AllowViolation list =
        let known = pairs baseline |> Set.ofList
        current |> List.filter (fun v -> not (known.Contains(v.FromElement, v.ToElement)))

    /// Debt: element pairs the baseline already crossed and the code still crosses. Distinct, in current order.
    /// Not blocking; alerted to Designc as `asbuilt_edge` so the model or the code can catch up.
    let debt (baseline: AllowViolation list) (current: AllowViolation list) : (ElementId * ElementId) list =
        let known = pairs baseline |> Set.ofList
        pairs current |> List.filter known.Contains |> List.distinct

    /// One line for a brief or a verify failure. It always names the missing relationship.
    let line (v: AllowViolation) : string =
        let verb = match v.Kind with ProjectReference -> "references" | AssemblyUse -> "uses types from" | Import -> "imports"
        $"- {v.Edge.Source} {verb} {v.Edge.Target}: {v.FromElement.Value} may not depend on {v.ToElement.Value}. The design has no relationship {v.FromElement.Value} → {v.ToElement.Value}: remove the dependency, or add the relationship in designc (a new handover)"
