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

/// A PR comment. `/approve <reason>` and `/verified <task>: <check>` count only from allow-listed logins.
type PrComment = { Author: string; Body: string }

type Approval =
    { Approved: bool
      /// Allow-listed logins whose /approve counted.
      By: string list
      /// One operator stood in for several approvers (distinct stated reasons); autonomy was
      /// lower than the policy says, so the ledger records it.
      SingleOperator: bool
      Missing: int }

/// What a person recorded for a manual check (`factory-s2 verify`). Deferred still clears the
/// block, and the note travels to the PR and the merge row.
type ManualOutcome =
    | Verified
    | Deferred of note: string

/// `Check` is the block's own text, "<task>: <check name>".
type ManualRecord = { Check: string; By: string; Outcome: ManualOutcome }

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

    let private required =
        function
        | AutoMerge -> 0
        | OneApproval -> 1
        | TwoApprovals -> 2

    let private allowed (approvers: string list) (login: string) =
        approvers |> List.exists (fun a -> System.String.Equals(a, login, System.StringComparison.OrdinalIgnoreCase))

    /// The text after `command` when the comment starts with it, trimmed.
    let private argument (command: string) (body: string) =
        let b = body.Trim()
        if b = command || b.StartsWith(command + " ") then Some(b.Substring(command.Length).Trim()) else None

    /// Spec: tests starting "approve:". Distinct allow-listed logins with an /approve comment count
    /// toward the tier. With exactly one allow-listed login, that operator may meet a tier above
    /// one approval with one /approve per distinct stated reason (single-operator override).
    let approvals (tier: MergeTier) (approvers: string list) (comments: PrComment list) : Approval =
        let need = required tier
        let approves = comments |> List.choose (fun c -> if allowed approvers c.Author then argument "/approve" c.Body |> Option.map (fun r -> c.Author.ToLowerInvariant(), r) else None)
        let by = approves |> List.map fst |> List.distinct
        match approvers with
        | [ _ ] when need > 1 && by.Length < need ->
            let reasons = approves |> List.map snd |> List.filter (fun r -> r <> "") |> List.distinct
            let counted = min need reasons.Length
            { Approved = counted >= need; By = by; SingleOperator = counted >= need; Missing = need - counted }
        | _ ->
            let counted = min need by.Length
            { Approved = counted >= need; By = by; SingleOperator = false; Missing = need - counted }

    /// Drops ManualVerifyPending blocks an allow-listed `/verified <check>` comment records as done.
    let recordVerified (approvers: string list) (comments: PrComment list) (verdict: MergeVerdict) : MergeVerdict =
        let verified = comments |> List.choose (fun c -> if allowed approvers c.Author then argument "/verified" c.Body else None) |> Set.ofList
        { verdict with Blocks = verdict.Blocks |> List.filter (function ManualVerifyPending c -> not (verified.Contains c) | _ -> true) }

    /// The manual records that count, the latest per check: in Tiered mode only an approver's,
    /// in Auto mode anyone's who gave a name.
    let countedManual (enforcement: MergeEnforcement) (records: ManualRecord list) : ManualRecord list =
        let may (by: string) =
            match enforcement with
            | Auto -> by.Trim() <> ""
            | Tiered approvers -> allowed approvers by
        records |> List.filter (fun r -> may r.By) |> List.groupBy (fun r -> r.Check) |> List.map (snd >> List.last)

    /// Drops ManualVerifyPending blocks a counted manual record covers, whether verified or deferred.
    let recordManual (enforcement: MergeEnforcement) (records: ManualRecord list) (verdict: MergeVerdict) : MergeVerdict =
        let recorded = countedManual enforcement records |> List.map (fun r -> r.Check) |> Set.ofList
        { verdict with Blocks = verdict.Blocks |> List.filter (function ManualVerifyPending c -> not (recorded.Contains c) | _ -> true) }
