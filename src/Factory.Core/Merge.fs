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

    /// TODO(you) — RESOURCES.md §5. Spec: tests starting "merge:".
    /// Produce (tier, reason) pairs from these rules, then take the highest tier:
    ///   - touched element with Risk = High                          -> TwoApprovals
    ///   - touched element riskier than policy.AutoMergeMaxRisk      -> OneApproval
    ///   - any Manual verify entry in any task                        -> OneApproval
    ///   - ChangedLines > policy.AutoMergeMaxChangedLines             -> OneApproval
    ///   - a changed file matched by no team's Writes                 -> TwoApprovals
    /// No rules fired -> AutoMerge with one reason saying why.
    /// Later: these thresholds are policy; compile them from mlang instead of defaultPolicy.
    let decide (policy: MergePolicy) (input: MergeInput) : MergeVerdict =
        ignore (policy, input, rank, tierRank)
        failwith "TODO: Merge.decide"
