namespace Factory.Core

/// How a run closes its handover in the registry (designc `handover_closed`). Qualified so it
/// never reads as FailureReason.Abandoned.
[<RequireQualifiedAccess>]
type HandoverOutcome =
    | Delivered
    | Abandoned

/// One run of a bound decision's check (its `Run` command) on the integration branch.
type CheckResult =
    { Decision: DecisionId
      Check: string
      Passed: bool }

module HandoverClosure =
    /// Delivered only when the code merged and the latest run of every check of every bound
    /// decision passed. A check that never ran counts as not passed.
    let outcome (decisions: BoundDecision list) (merged: bool) (results: CheckResult list) : HandoverOutcome =
        let latest =
            results |> List.fold (fun m r -> Map.add (r.Decision, r.Check) r.Passed m) Map.empty

        let everyCheckPassed =
            decisions
            |> List.forall (fun d -> d.Checks |> List.forall (fun c -> Map.tryFind (d.Id, c.Run) latest = Some true))

        if merged && everyCheckPassed then HandoverOutcome.Delivered else HandoverOutcome.Abandoned
