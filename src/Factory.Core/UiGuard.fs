namespace Factory.Core

type UiItemKind =
    | TestId
    | Route
    | UiTest
    | SkippedTest

/// One thing the UI offers, found by a heuristic scan at the edge: a data-testid, a route, or the title of a UI test.
/// A skipped test is listed as SkippedTest, never as UiTest.
type UiItem = { Kind: UiItemKind; Id: string }

type UiViolation =
    | Removed of UiItem
    | NowSkipped of UiItem
    | ContractMissing of UiItem

[<RequireQualifiedAccess>]
module UiGuard =

    /// What the task took away. A contract item missing now is always a violation (even if the baseline never had it).
    /// A baseline item missing now is Removed, or NowSkipped when the test is still there but skipped. Anything in
    /// `authorised` (a human accepted the removal) is clean. Skipped tests are debt on the baseline and never a lost feature.
    let check (contract: UiItem list) (authorised: UiItem list) (baseline: UiItem list) (current: UiItem list) : UiViolation list =
        let cur = Set.ofList current
        let ok = Set.ofList authorised
        let gone (i: UiItem) = not (cur.Contains i) && not (ok.Contains i)
        let missing =
            contract |> List.filter (fun i -> i.Kind <> SkippedTest && gone i)
        let lost =
            baseline
            |> List.filter (fun i -> i.Kind <> SkippedTest && gone i && not (List.contains i missing))
        let violation (i: UiItem) =
            if i.Kind = UiTest && cur.Contains { i with Kind = SkippedTest } then NowSkipped i else Removed i
        [ for i in missing |> List.distinct -> ContractMissing i
          for i in lost |> List.distinct -> violation i ]
