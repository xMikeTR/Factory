namespace Factory.Core

/// A dependency one piece of code has on another, as an adapter found it (a project reference, a
/// using). Both ends are repo-relative paths.
type CodeEdge = { Source: string; Target: string }

type FitnessViolation =
    { Edge: CodeEdge
      FromElement: ElementId
      ToElement: ElementId }

/// A dependency the repo's own policy forbids, by path globs (hand-written in the protected policy file).
type ForbiddenDependency = { From: PathPattern; To: PathPattern; Why: string }

type RuleViolation = { Edge: CodeEdge; Rule: ForbiddenDependency }

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

    /// Edges the hand-written rules forbid.
    let forbidden (rules: ForbiddenDependency list) (edges: CodeEdge list) : RuleViolation list =
        [ for edge in edges do
            for rule in rules do
                if PathPattern.matches rule.From edge.Source && PathPattern.matches rule.To edge.Target then
                    yield { Edge = edge; Rule = rule } ]

    /// The ratchet again: an edge the baseline already had under the same rule is debt.
    let newRuleViolationsSince (baseline: RuleViolation list) (current: RuleViolation list) : RuleViolation list =
        let known = baseline |> List.map (fun v -> v.Edge, v.Rule) |> Set.ofList
        current |> List.filter (fun v -> not (known.Contains(v.Edge, v.Rule)))
