namespace Factory.Core

type MergeTier =
    | AutoMerge
    | OneApproval
    | TwoApprovals

/// Auto: merge on green, the tier is only reported. Tiered: the tier decides how many of the
/// approvers must approve before the merge.
type MergeEnforcement =
    | Auto
    | Tiered of approvers: string list

/// Reasons a branch never merges, whatever the enforcement mode.
type MergeBlock =
    | ScopeLeak of path: string
    | ProtectedPathChanged of path: string
    | ManualVerifyPending of check: string

/// Thresholds for tiered merge autonomy, read from the target repo's .factory/policy.yaml.
type MergePolicy =
    { Enforcement: MergeEnforcement
      AutoMergeMaxRisk: RiskLevel
      AutoMergeMaxChangedLines: int }

type MergeInput =
    { Teams: Team list
      Handover: Handover
      ChangedLines: int
      /// Paths changed by the branch, from `git diff --name-only`.
      ChangedFiles: string list }

/// Any block = no merge, in every mode.
type MergeVerdict =
    { Tier: MergeTier
      Reasons: string list
      Blocks: MergeBlock list }

[<RequireQualifiedAccess>]
type MergeAction =
    | MergeNow
    | AwaitApproval of tier: MergeTier * approvers: string list
    | Blocked of MergeBlock list

[<RequireQualifiedAccess>]
module Merge =

    let defaultPolicy =
        { Enforcement = Auto
          AutoMergeMaxRisk = Low
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
    ///   - ChangedLines > policy.AutoMergeMaxChangedLines             -> OneApproval
    /// No rules fired -> AutoMerge with one reason saying why.
    /// Blocks, independent of the tier:
    ///   - a changed file matched by no team's Writes                 -> ScopeLeak
    ///   - a changed file under a handover protected path             -> ProtectedPathChanged
    ///   - any Manual verify entry in any task                        -> ManualVerifyPending
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
              if input.ChangedLines > policy.AutoMergeMaxChangedLines then
                  OneApproval, $"{input.ChangedLines} changed lines (auto-merge allows up to {policy.AutoMergeMaxChangedLines})"
            ]

        let blocks =
            [ for f in input.ChangedFiles do
                  if input.Handover.ProtectedPaths |> List.exists (fun p -> PathPattern.matches p f) then ProtectedPathChanged f
                  elif not (writes |> List.exists (fun w -> PathPattern.matches w f)) then ScopeLeak f
              for (t, name) in manual do
                  ManualVerifyPending $"{t.Value}: {name}" ]

        match fired with
        | [] ->
            { Tier = AutoMerge
              Reasons = [ $"all touched elements at or below {policy.AutoMergeMaxRisk} risk, within the changed-line limit" ]
              Blocks = blocks }
        | _ ->
            { Tier = fired |> List.map fst |> List.maxBy tierRank
              Reasons = fired |> List.map snd
              Blocks = blocks }

    /// What the merge step does with a verdict. Blocks win in every mode.
    let action (policy: MergePolicy) (verdict: MergeVerdict) : MergeAction =
        match verdict.Blocks, policy.Enforcement with
        | (_ :: _) as blocks, _ -> MergeAction.Blocked blocks
        | [], Auto -> MergeAction.MergeNow
        | [], Tiered _ when verdict.Tier = AutoMerge -> MergeAction.MergeNow
        | [], Tiered approvers -> MergeAction.AwaitApproval(verdict.Tier, approvers)

    let describe =
        function
        | ScopeLeak f -> $"'{f}' changed but no team declared it in writes"
        | ProtectedPathChanged f -> $"'{f}' is under a protected path"
        | ManualVerifyPending c -> $"manual check pending: {c}"
