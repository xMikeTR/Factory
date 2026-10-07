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
        match Json.parsePlan planJson with
        | Error es -> PlanValidation(null, es |> List.map (fun e -> "Plan JSON: " + e) |> Array.ofList)
        | Ok plan ->
            match PlanValidator.validate handover plan with
            | Ok vp -> PlanValidation(vp, [||])
            | Error es -> PlanValidation(null, es |> List.map PlanError.describe |> Array.ofList)

    static member Dispatch(plan: ValidatedPlan) : DispatchPlan = Dispatcher.dispatch plan

    static member DispatchJson(d: DispatchPlan) : string = Json.writeDispatch d

    static member Queued = TaskState.queued

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

    static member Matches(pattern: PathPattern, path: string) = PathPattern.matches pattern path

    static member ToList(xs: 'a seq) : 'a list = List.ofSeq xs
