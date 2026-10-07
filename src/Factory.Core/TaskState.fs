namespace Factory.Core

type Limits = { MaxTurns: int; MaxVerifyAttempts: int }

type Progress = { Turns: int; VerifyAttempts: int }

type NeedsDecision =
    { Question: string
      Elements: ElementId list
      /// Why the task stopped: the agent asked, or the configurator saw a stall.
      Source: string }

type VerifyOutcome =
    { Name: string
      ExitCode: int
      DurationMs: int64
      OutputTail: string }

/// Why the tests failed before any implementation existed.
type RedKind =
    | BuildFailure
    | AssertionFailure

/// Proof that the TestAuthor's tests fail against the code as it was before the Implementer
/// started. Only constructible from a run with at least one non-zero outcome: a test that
/// already passes proves nothing about the change.
type RedEvidence =
    private
        { outcomes: VerifyOutcome list
          kind: RedKind }

    member this.Outcomes = this.outcomes
    member this.Kind = this.kind

    static member Create(outcomes: VerifyOutcome list, kind: RedKind) : Result<RedEvidence, string> =
        match outcomes with
        | [] -> Error "no red run outcomes"
        | os when os |> List.forall (fun o -> o.ExitCode = 0) ->
            Error("every test passed before implementation, so none of them proves the change: " + System.String.Join(", ", os |> List.map (fun o -> o.Name)))
        | os -> Ok { outcomes = os; kind = kind }

/// Reads the TestAuthor's red run before it becomes evidence.
[<RequireQualifiedAccess>]
module RedRun =

    let private matches (pattern: string) (text: string) =
        System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)

    /// The sandbox could not run the check: a package source it cannot reach (verify runs with
    /// the network off), or a command the image does not have. No code change fixes either.
    let private environmental (o: VerifyOutcome) =
        o.ExitCode = 127 || matches @"error NU1301\b" o.OutputTail

    /// Error: the checks the sandbox could not run. One is enough: it can never go green, so the
    /// Implementer would spend every turn against it. Ok: the kind of red (a compile failure
    /// proves less than a failing assertion); RedEvidence.Create still refuses a run with no red.
    let classify (outcomes: VerifyOutcome list) : Result<RedKind, string list> =
        let failed = outcomes |> List.filter (fun o -> o.ExitCode <> 0)
        match failed |> List.filter environmental with
        | [] when failed |> List.exists (fun o -> matches @"error (CS|FS)\d+|Build FAILED" o.OutputTail) -> Ok BuildFailure
        | [] -> Ok AssertionFailure
        | env -> Error(env |> List.map (fun o -> o.Name))

/// Who holds the pen. The Implementer phase cannot exist without the red evidence that ended
/// the TestAuthor phase.
type Phase =
    | TestAuthor
    | Implementer of RedEvidence

/// Proof that a task passed. Three gates: the tests were red before implementation, every
/// automated check is green now, and with the implementation stashed at least one check fails
/// again (so the tests exercise the change rather than pass vacuously).
[<Sealed>]
type PassedEvidence private (attempt: int, green: VerifyOutcome list, red: RedEvidence, stashedImpl: VerifyOutcome list) =
    member _.Attempt = attempt
    member _.Outcomes = green
    member _.Red = red
    member _.StashedImpl = stashedImpl

    static member Create(attempt: int, green: VerifyOutcome list, red: RedEvidence, stashedImpl: VerifyOutcome list) : Result<PassedEvidence, string> =
        match green with
        | [] -> Error "no verify outcomes"
        | os when os |> List.exists (fun o -> o.ExitCode <> 0) ->
            let failed = os |> List.filter (fun o -> o.ExitCode <> 0) |> List.map (fun o -> o.Name)
            Error("failed: " + System.String.Join(", ", failed))
        | _ when stashedImpl |> List.forall (fun o -> o.ExitCode = 0) ->
            Error "stashed-impl check: with the implementation stashed every test still passes, so the tests do not exercise it"
        | _ -> Ok(PassedEvidence(attempt, green, red, stashedImpl))

type FailureReason =
    | TurnBudgetExhausted of turns: int
    | VerifyBudgetExhausted of lastOutcomes: VerifyOutcome list
    | Abandoned of reason: string

type TaskState =
    /// Waiting for the next turn. `Ready {0;0}` is the queued state.
    | Ready of Progress * Phase
    | Running of Progress * Phase * ModelTier
    | Blocked of Progress * Phase * NeedsDecision
    | Verifying of Progress * Phase
    | Passed of PassedEvidence
    | Failed of FailureReason * Progress

    member this.IsTerminal =
        match this with
        | Passed _
        | Failed _ -> true
        | _ -> false

type TaskEvent =
    | TurnStarted of ModelTier
    | TurnEnded
    | NeedsDecisionRaised of NeedsDecision
    | DecisionBound of DecisionId
    | VerifyRequested
    /// Green run plus the stashed-impl run (empty when green failed and the stash was skipped).
    | VerifyCompleted of green: VerifyOutcome list * stashedImpl: VerifyOutcome list
    /// TestAuthor -> Implementer. Carries the red run, so it cannot be raised without one.
    | TestsAuthored of RedEvidence
    /// Implementer -> TestAuthor: the tests or checks are broken in a way only the TestAuthor's
    /// write paths can fix. The red evidence goes with the phase; the tests need a new red run.
    | ReturnedToTestAuthor of problem: string
    | Abandon of reason: string

type TransitionError = { State: string; Event: string }

[<RequireQualifiedAccess>]
module TaskState =

    let queued = Ready({ Turns = 0; VerifyAttempts = 0 }, TestAuthor)

    let private name (s: TaskState) =
        match s with
        | Ready _ -> "Ready"
        | Running _ -> "Running"
        | Blocked _ -> "Blocked"
        | Verifying _ -> "Verifying"
        | Passed _ -> "Passed"
        | Failed _ -> "Failed"

    let private eventName (e: TaskEvent) =
        match e with
        | TurnStarted _ -> "TurnStarted"
        | TurnEnded -> "TurnEnded"
        | NeedsDecisionRaised _ -> "NeedsDecisionRaised"
        | DecisionBound _ -> "DecisionBound"
        | VerifyRequested -> "VerifyRequested"
        | VerifyCompleted _ -> "VerifyCompleted"
        | TestsAuthored _ -> "TestsAuthored"
        | ReturnedToTestAuthor _ -> "ReturnedToTestAuthor"
        | Abandon _ -> "Abandon"

    /// The whole task lifecycle. Budgets and separation of duties are enforced here, not by
    /// agents or prompts. Spec: every test starting "state:".
    /// Transition table (anything not listed is illegal -> Error { State; Event }):
    ///   Passed | Failed            + anything              -> illegal (terminal)
    ///   any non-terminal           + Abandon r             -> Failed(Abandoned r, progress)
    ///   Ready(p,ph)                + TurnStarted tier      -> Failed(TurnBudgetExhausted) if p.Turns >= MaxTurns
    ///                                                         else Running({p with Turns+1}, ph, tier)
    ///   Running(p,ph,_)            + TurnEnded             -> Ready(p, ph)
    ///   Running(p,ph,_) | Ready    + NeedsDecisionRaised n -> Blocked(p, ph, n)
    ///   Blocked(p,ph,_)            + DecisionBound _       -> Ready(p, ph)
    ///   Ready(p,TestAuthor)        + TestsAuthored red     -> Ready(p, Implementer red)
    ///   Ready(p,Implementer)       + ReturnedToTestAuthor  -> Ready(p, TestAuthor)
    ///   Ready(p,Implementer)       + VerifyRequested       -> Failed(VerifyBudgetExhausted []) if p.VerifyAttempts >= Max
    ///                                                         else Verifying({p with VerifyAttempts+1}, ph)
    ///   Verifying(p,Implementer r) + VerifyCompleted(g,s)  -> Passed ev   if PassedEvidence.Create succeeds
    ///                                                      -> Failed(VerifyBudgetExhausted) if attempts used up
    ///                                                      -> Ready(p, TestAuthor) if green but the stash check failed
    ///                                                      -> Ready(p, Implementer r) otherwise
    let transition (limits: Limits) (state: TaskState) (event: TaskEvent) : Result<TaskState, TransitionError> =
        let illegal () = Error { State = name state; Event = eventName event }
        // Outer match on state, inner on event, no wildcards: adding a case to either DU is a
        // compile error here until the table says what it means.
        match state with
        | Passed _
        | Failed _ -> illegal ()
        | Ready(p, ph) ->
            match event with
            | Abandon r -> Ok(Failed(Abandoned r, p))
            | TurnStarted tier ->
                if p.Turns >= limits.MaxTurns then Ok(Failed(TurnBudgetExhausted p.Turns, p))
                else Ok(Running({ p with Turns = p.Turns + 1 }, ph, tier))
            | NeedsDecisionRaised n -> Ok(Blocked(p, ph, n))
            | TestsAuthored red ->
                match ph with
                | TestAuthor -> Ok(Ready(p, Implementer red))
                | Implementer _ -> illegal ()
            | ReturnedToTestAuthor _ ->
                match ph with
                | TestAuthor -> illegal ()
                | Implementer _ -> Ok(Ready(p, TestAuthor))
            | VerifyRequested ->
                match ph with
                | TestAuthor -> illegal ()
                | Implementer _ ->
                    if p.VerifyAttempts >= limits.MaxVerifyAttempts then Ok(Failed(VerifyBudgetExhausted [], p))
                    else Ok(Verifying({ p with VerifyAttempts = p.VerifyAttempts + 1 }, ph))
            | TurnEnded
            | DecisionBound _
            | VerifyCompleted _ -> illegal ()
        | Running(p, ph, _) ->
            match event with
            | Abandon r -> Ok(Failed(Abandoned r, p))
            | TurnEnded -> Ok(Ready(p, ph))
            | NeedsDecisionRaised n -> Ok(Blocked(p, ph, n))
            | TurnStarted _
            | DecisionBound _
            | VerifyRequested
            | VerifyCompleted _
            | TestsAuthored _
            | ReturnedToTestAuthor _ -> illegal ()
        | Blocked(p, ph, _) ->
            match event with
            | Abandon r -> Ok(Failed(Abandoned r, p))
            | DecisionBound _ -> Ok(Ready(p, ph))
            | TurnStarted _
            | TurnEnded
            | NeedsDecisionRaised _
            | VerifyRequested
            | VerifyCompleted _
            | TestsAuthored _
            | ReturnedToTestAuthor _ -> illegal ()
        | Verifying(p, ph) ->
            match event with
            | Abandon r -> Ok(Failed(Abandoned r, p))
            | VerifyCompleted(green, stashed) ->
                match ph with
                | TestAuthor -> illegal ()
                | Implementer red ->
                    let greenOk = not green.IsEmpty && green |> List.forall (fun o -> o.ExitCode = 0)
                    match PassedEvidence.Create(p.VerifyAttempts, green, red, stashed) with
                    | Ok ev -> Ok(Passed ev)
                    | Error _ when p.VerifyAttempts >= limits.MaxVerifyAttempts ->
                        Ok(Failed(VerifyBudgetExhausted(if greenOk then stashed else green), p))
                    | Error _ when greenOk -> Ok(Ready(p, TestAuthor))
                    | Error _ -> Ok(Ready(p, ph))
            | TurnStarted _
            | TurnEnded
            | NeedsDecisionRaised _
            | DecisionBound _
            | VerifyRequested
            | TestsAuthored _
            | ReturnedToTestAuthor _ -> illegal ()

    /// Rebuild a task's state from its ledger events. This is how a restarted runner resumes.
    let replay (limits: Limits) (events: TaskEvent seq) : Result<TaskState, TransitionError> =
        events
        |> Seq.fold (fun acc e -> acc |> Result.bind (fun s -> transition limits s e)) (Ok queued)
