namespace Factory.Core

type QaKind =
    /// Behaviour the run adds: red on the baseline, green on the integrated result.
    | New
    /// Behaviour the run must not change: green on both.
    | Characterisation

type QaOutcome =
    | QaPassed
    | QaFailed

/// One black-box test, written at requirement level, run on the baseline and on the integrated result.
type QaTest =
    { Id: string
      Requirement: string
      Kind: QaKind
      OnBaseline: QaOutcome
      OnIntegrated: QaOutcome }

type QaFinding =
    /// A requirement with no `new` test: nothing shows the run delivered it.
    | Uncovered of requirement: string
    /// A `new` test that already passes on the baseline proves nothing about this run.
    | Vacuous of test: string
    /// A `new` test that still fails on the integrated result: the requirement is not met.
    | NotDone of test: string
    /// A characterisation test that passed before and fails now: a regression the task gates let through.
    | Escape of test: string
    /// A characterisation test that fails on the baseline cannot describe existing behaviour.
    | InvalidCharacterisation of test: string

[<RequireQualifiedAccess>]
module Qa =

    /// The author's turn budget (it is the reviewer pool's, apart from the Implementer's).
    let authorTurns = 8

    let private judgeTest (t: QaTest) : QaFinding list =
        match t.Kind, t.OnBaseline, t.OnIntegrated with
        | New, QaFailed, QaPassed -> []
        | New, QaPassed, _ -> [ Vacuous t.Id ]
        | New, QaFailed, QaFailed -> [ NotDone t.Id ]
        | Characterisation, QaPassed, QaPassed -> []
        | Characterisation, QaPassed, QaFailed -> [ Escape t.Id ]
        | Characterisation, QaFailed, _ -> [ InvalidCharacterisation t.Id ]

    /// Empty means the integrated result may merge. Every requirement needs a `new` test that did its job
    /// (a vacuous or unfinished one does not count as covering it).
    let judge (requirements: string list) (tests: QaTest list) : QaFinding list =
        let covered = tests |> List.filter (fun t -> t.Kind = New && judgeTest t = []) |> List.map (fun t -> t.Requirement) |> Set.ofList
        let uncovered = requirements |> List.filter (fun r -> not (Set.contains r covered)) |> List.map Uncovered
        let perTest = tests |> List.collect judgeTest
        // A requirement whose only `new` test is vacuous or unfinished is reported once, through that test.
        let reportedViaTest =
            tests |> List.filter (fun t -> t.Kind = New && judgeTest t <> []) |> List.map (fun t -> t.Requirement) |> Set.ofList
        let uncoveredQuiet = uncovered |> List.filter (function Uncovered r -> not (Set.contains r reportedViaTest) | _ -> true)
        uncoveredQuiet @ perTest
