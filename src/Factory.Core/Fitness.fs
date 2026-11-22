namespace Factory.Core

/// A dependency one piece of code has on another, as an adapter found it (a project reference, a
/// using). Both ends are repo-relative paths.
type CodeEdge = { Source: string; Target: string }

/// A dependency the repo's own policy forbids, by path globs (hand-written in the protected policy file).
type ForbiddenDependency = { From: PathPattern; To: PathPattern; Why: string }

type RuleViolation = { Edge: CodeEdge; Rule: ForbiddenDependency }

/// Hand-written architecture rules (the policy file's `forbid`). The design's own allow list is `AllowedEdges`.
[<RequireQualifiedAccess>]
module Fitness =

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
