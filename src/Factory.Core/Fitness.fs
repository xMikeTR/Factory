namespace Factory.Core

/// A dependency one piece of code has on another, as an adapter found it (a project reference, a
/// using). Both ends are repo-relative paths.
type CodeEdge = { Source: string; Target: string }

type FitnessViolation =
    { Edge: CodeEdge
      FromElement: ElementId
      ToElement: ElementId }

/// Architecture fitness from the Designc model: code in one element's area may depend on another
/// element's code only if the model has that relationship (directed).
[<RequireQualifiedAccess>]
module Fitness =

    let private owner (elements: ElementSpec list) (path: string) =
        elements |> List.tryFind (fun e -> e.CodeAreas |> List.exists (fun a -> PathPattern.matches a path))

    /// Edges that cross between two elements without a relationship. Code no element owns is not judged.
    let violations (elements: ElementSpec list) (relationships: Relationship list) (edges: CodeEdge list) : FitnessViolation list =
        edges
        |> List.choose (fun edge ->
            match owner elements edge.Source, owner elements edge.Target with
            | Some a, Some b when a.Id <> b.Id && not (relationships |> List.contains { From = a.Id; To = b.Id }) ->
                Some { Edge = edge; FromElement = a.Id; ToElement = b.Id }
            | _ -> None)

    /// The ratchet: an element pair the baseline already crossed is debt, not the task's doing.
    let newSince (baseline: FitnessViolation list) (current: FitnessViolation list) : FitnessViolation list =
        let known = baseline |> List.map (fun v -> v.FromElement, v.ToElement) |> Set.ofList
        current |> List.filter (fun v -> not (known.Contains(v.FromElement, v.ToElement)))
