namespace Factory.Core

type MergeTier =
    | AutoMerge
    | OneApproval
    | TwoApprovals

/// Thresholds for tiered merge autonomy. Hard-coded defaults for now; the intent is for these
/// to come from an mlang contract so the policy is governed like everything else.
type MergePolicy =
    { AutoMergeMaxRisk: RiskLevel
      AutoMergeMaxChangedLines: int }

type MergeInput =
    { Teams: Team list
      Handover: Handover
      ChangedLines: int
      /// Paths changed by the branch, from `git diff --name-only`.
      ChangedFiles: string list }

type MergeVerdict =
    { Tier: MergeTier
      Reasons: string list }

[<RequireQualifiedAccess>]
module Merge =

    let defaultPolicy =
        { AutoMergeMaxRisk = Low
          AutoMergeMaxChangedLines = 400 }

    let private rank =
        function
        | Low -> 0
        | Medium -> 1
        | High -> 2

    let private tierRank =
        function
        | AutoMerge -> 0
        | OneApproval -> 1
        | TwoApprovals -> 2

    /// RESOURCES.md §5. Spec: tests starting "merge:".
    /// Produce (tier, reason) pairs from these rules, then take the highest tier:
    ///   - touched element with Risk = High                          -> TwoApprovals
    ///   - touched element riskier than policy.AutoMergeMaxRisk      -> OneApproval
    ///   - any Manual verify entry in any task                        -> OneApproval
    ///   - ChangedLines > policy.AutoMergeMaxChangedLines             -> OneApproval
    ///   - a changed file matched by no team's Writes                 -> TwoApprovals
    /// No rules fired -> AutoMerge with one reason saying why.
    /// Later: these thresholds are policy; compile them from mlang instead of defaultPolicy.
    let decide (policy: MergePolicy) (input: MergeInput) : MergeVerdict =
        let touchedIds = input.Teams |> List.collect (fun t -> t.Elements) |> Set.ofList
        let touched = input.Handover.Elements |> List.filter (fun e -> touchedIds.Contains e.Id)
        let writes = input.Teams |> List.collect (fun t -> t.Writes)
        let manual =
            input.Teams
            |> List.collect (fun t -> t.Tasks)
            |> List.collect (fun t -> t.Verify |> List.choose (function Manual m -> Some(t.Id, m.Name) | Automated _ -> None))

        let fired =
            [ for e in touched do
                  if e.Risk = High then TwoApprovals, $"element '{e.Name}' is high risk"
                  elif rank e.Risk > rank policy.AutoMergeMaxRisk then
                      OneApproval, $"element '{e.Name}' is {e.Risk} risk (auto-merge allows up to {policy.AutoMergeMaxRisk})"
              for (t, name) in manual do
                  OneApproval, $"task {t.Value} has a manual check '{name}'"
              if input.ChangedLines > policy.AutoMergeMaxChangedLines then
                  OneApproval, $"{input.ChangedLines} changed lines (auto-merge allows up to {policy.AutoMergeMaxChangedLines})"
              for f in input.ChangedFiles do
                  if not (writes |> List.exists (fun w -> PathPattern.matches w f)) then
                      TwoApprovals, $"'{f}' changed but no team declared it in writes" ]

        match fired with
        | [] ->
            { Tier = AutoMerge
              Reasons = [ $"all touched elements at or below {policy.AutoMergeMaxRisk} risk, no manual checks, every changed file declared" ] }
        | _ ->
            { Tier = fired |> List.map fst |> List.maxBy tierRank
              Reasons = fired |> List.map snd }
