namespace Factory.Core

/// One test from a suite run (parsed from JUnit XML at the edge).
type TestResult = { Name: string; Passed: bool }

[<RequireQualifiedAccess>]
module Regression =

    let private passing (results: TestResult list) =
        results
        |> List.groupBy (fun r -> r.Name)
        |> List.filter (fun (_, runs) -> runs |> List.forall (fun r -> r.Passed))
        |> List.map fst
        |> Set.ofList

    /// Pass-to-pass check: names that passed in the baseline (every run of them) and do not pass
    /// now. A name missing now counts: the test was deleted, or the suite did not build. Tests
    /// that failed in the baseline, or are new, are never regressions. In baseline order.
    let regressions (baseline: TestResult list) (current: TestResult list) : string list =
        let before = passing baseline
        let now = passing current
        baseline
        |> List.map (fun r -> r.Name)
        |> List.distinct
        |> List.filter (fun n -> before.Contains n && not (now.Contains n))
