namespace Factory.Core

open System

/// Thrown when an input fails a deterministic check. Errors are written for a human or an
/// agent to act on directly.
type FactoryRejectedException(what: string, errors: string[]) =
    inherit Exception(what + ":\n  - " + String.Join("\n  - ", errors))
    member _.Errors = errors

[<Sealed>]
type PlanValidation(plan: ValidatedPlan, errors: string[]) =
    member _.IsValid = not (isNull plan)
    /// null when invalid
    member _.Plan = plan
    member _.Errors = errors

/// The C# host's view of the F# core: arrays, exceptions and nulls instead of lists and Results.
[<AbstractClass; Sealed>]
type Core =

    static member ParseHandover(json: string) : Handover =
        match Json.parseHandover json with
        | Ok h -> h
        | Error es -> raise (FactoryRejectedException("handover package is malformed", Array.ofList es))

    static member ValidatePlan(handover: Handover, planJson: string) : PlanValidation =
        Core.ValidatePlan(handover, planJson, Array.empty)

    /// With the repo names the run was given (`--repo Api=.. --repo Ui=..`); an empty array leaves repos unchecked.
    static member ValidatePlan(handover: Handover, planJson: string, knownRepos: string[]) : PlanValidation =
        match Json.parsePlan planJson with
        | Error es -> PlanValidation(null, es |> List.map (fun e -> "Plan JSON: " + e) |> Array.ofList)
        | Ok plan ->
            match PlanValidator.validateWithRepos (List.ofArray knownRepos) handover plan with
            | Ok vp -> PlanValidation(vp, [||])
            | Error es -> PlanValidation(null, es |> List.map PlanError.describe |> Array.ofList)

    static member Dispatch(plan: ValidatedPlan) : DispatchPlan = Dispatcher.dispatch plan

    /// Which of the plan's tasks a re-frozen handover re-runs (see Regeneration.between).
    static member Regenerate(before: Handover, plan: ValidatedPlan) : Regeneration =
        Regeneration.between before plan.Handover plan.Plan

    static member DispatchJson(d: DispatchPlan) : string = Json.writeDispatch d

    static member HandoverJson(h: Handover) : string = Json.writeHandover h

    static member Queued = TaskState.queued

    static member MaxReviewRounds = TaskState.maxReviewRounds

    static member Transition(limits: Limits, state: TaskState, ev: TaskEvent) : TaskState =
        match TaskState.transition limits state ev with
        | Ok s -> s
        | Error e -> raise (InvalidOperationException($"illegal task transition: {e.Event} in state {e.State}"))

    /// Rebuild state from ledger payloads (JSON produced by EncodeEvent).
    static member Replay(limits: Limits, payloads: string seq) : TaskState =
        let events =
            payloads
            |> Seq.map (fun p ->
                match Json.decodeTaskEvent p with
                | Ok e -> e
                | Error es -> raise (FactoryRejectedException("unreadable task event in ledger", Array.ofList es)))
        match TaskState.replay limits events with
        | Ok s -> s
        | Error e -> raise (InvalidOperationException($"ledger replay hit an illegal transition: {e.Event} in state {e.State}"))

    static member EncodeEvent(ev: TaskEvent) : string = Json.encodeTaskEvent ev

    static member DecideMerge(policy: MergePolicy, input: MergeInput) : MergeVerdict = Merge.decide policy input

    static member DefaultMergePolicy = Merge.defaultPolicy
    static member MergeActionFor(policy: MergePolicy, verdict: MergeVerdict) : MergeAction = Merge.action policy verdict
    static member DescribeBlock(block: MergeBlock) : string = Merge.describe block
    static member Approvals(tier: MergeTier, approvers: string list, comments: PrComment seq) : Approval = Merge.approvals tier approvers (List.ofSeq comments)
    static member RecordVerified(approvers: string list, comments: PrComment seq, verdict: MergeVerdict) : MergeVerdict = Merge.recordVerified approvers (List.ofSeq comments) verdict

    static member Matches(pattern: PathPattern, path: string) = PathPattern.matches pattern path

    static member ToList(xs: 'a seq) : 'a list = List.ofSeq xs

    /// Why a changed restore input stops the restore window; empty when it may restore. Null before = new file.
    static member ReviewRestoreInput(path: string, before: string, after: string) : string list =
        let review = if PackageJsonChange.affects path then PackageJsonChange.review else ProjectFileChange.review
        match review path (Option.ofObj before) (Some after) with
        | Ok() -> []
        | Error errors -> errors

    /// Errors in the options of a raised decision; empty when they make a real choice. Null recommended = none.
    static member DecisionOptionErrors(options: DecisionOption seq, recommended: string) : string list =
        NeedsDecision.optionErrors (List.ofSeq options) (Option.ofObj recommended)

    /// The answer for the agent, from the designer's chosen option and note (either may be null).
    static member AnswerDecision(nd: NeedsDecision, option: string, note: string) : Result<string, string> =
        NeedsDecision.answer nd (Option.ofObj option) (Option.ofObj note)

    /// A task's end and rounds from its ledger payloads, replayed with the run's limits (as a resume does).
    static member ReplayTrace(limits: Limits, payloads: string seq) : ReplayedTask =
        let events =
            payloads
            |> Seq.map (fun p ->
                match Json.decodeTaskEvent p with
                | Ok e -> e
                | Error es -> raise (FactoryRejectedException("unreadable task event in ledger", Array.ofList es)))
            |> List.ofSeq
        match Trace.replayed limits events with
        | Ok r -> r
        | Error e -> raise (InvalidOperationException($"ledger replay hit an illegal transition: {e.Event} in state {e.State}"))

    /// The task's trace; null while the task is still in flight.
    static member BuildTrace(input: TraceInput) : TaskTrace =
        match Trace.build input with
        | Some t -> t
        | None -> Unchecked.defaultof<TaskTrace>

    /// designc's spelling: stalled | parked | abandoned | gate_failed:<gate>.
    static member CauseToLedger(cause: TraceCause) : string = TraceCause.toLedger cause

    /// A seed that is the same on every machine and run for the same text (unlike string.GetHashCode):
    /// the first 8 hex of hash16, so a resumed run seeds the router the same way.
    static member StableSeed(text: string) : int =
        int (Convert.ToUInt32((Hash16.compute text).Substring(0, 8), 16))

    /// Router.taskSpec with a .NET sequence for the implementer's families.
    static member TaskSpecFor(config: TaskSpecConfig, handover: Handover, task: PlannedTask, role: Role, contextChars: int, implementerFamilies: string seq) : TaskSpec =
        Router.taskSpec config handover task role contextChars (Set.ofSeq implementerFamilies)

    /// Router.route with no learned stats and an Ok budget (phase 2), seeded so the same seed gives the same ladder.
    static member RouteTask(policy: RoutingPolicy, seed: int, spec: TaskSpec, catalog: ModelEntry seq) : RouteOutcome =
        let rng = Random(seed)
        Router.route policy rng.NextDouble Map.empty BudgetState.Ok spec (List.ofSeq catalog)

    /// Router.route with learned stats, then the canary draw from the same seeded rng (after the route draws, so
    /// the ladder does not depend on the share). The flag says the Standard entry was swapped for Frontier.
    static member RouteTaskLearned(policy: RoutingPolicy, seed: int, spec: TaskSpec, catalog: ModelEntry seq, stats: Stats, canaryShare: float) : RouteOutcome * bool =
        Core.RouteTaskBudgeted(policy, seed, spec, catalog, stats, canaryShare, BudgetState.Ok)

    /// RouteTaskLearned under a budget state (phase 4): Downgrade caps the tier at Standard, Blocked refuses.
    static member RouteTaskBudgeted(policy: RoutingPolicy, seed: int, spec: TaskSpec, catalog: ModelEntry seq, stats: Stats, canaryShare: float, budget: BudgetState) : RouteOutcome * bool =
        let rng = Random(seed)
        match Router.route policy rng.NextDouble stats budget spec (List.ofSeq catalog) with
        | RouteOutcome.Routed(ladder, warnings) ->
            let ladder', canary = Router.applyCanary canaryShare rng.NextDouble ladder
            RouteOutcome.Routed(ladder', warnings), canary
        | other -> other, false

    /// Learning.attribute for C#: events in ledger order, a predicate for catalog model ids.
    static member Attribute(known: Func<string, bool>, learnReviewer: bool, events: LearnEvent seq) : Attempt list =
        Learning.attribute known.Invoke learnReviewer (List.ofSeq events)

    /// Learning.fold from empty stats: attempts weighted by age at `now`.
    static member FoldAttempts(now: DateTime, halfLifeDays: float, attempts: Attempt seq) : Stats =
        Learning.fold now halfLifeDays Map.empty (List.ofSeq attempts)

