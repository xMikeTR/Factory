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

/// Proof that a task passed. Only constructible from a non-empty set of automated outcomes that
/// all exited 0, so `Passed` cannot exist without the evidence that justifies it.
[<Sealed>]
type PassedEvidence private (attempt: int, outcomes: VerifyOutcome list) =
    member _.Attempt = attempt
    member _.Outcomes = outcomes

    static member Create(attempt: int, outcomes: VerifyOutcome list) : Result<PassedEvidence, string> =
        match outcomes with
        | [] -> Error "no verify outcomes"
        | os when os |> List.forall (fun o -> o.ExitCode = 0) -> Ok(PassedEvidence(attempt, os))
        | os ->
            let failed = os |> List.filter (fun o -> o.ExitCode <> 0) |> List.map (fun o -> o.Name)
            Error("failed: " + System.String.Join(", ", failed))

type FailureReason =
    | TurnBudgetExhausted of turns: int
    | VerifyBudgetExhausted of lastOutcomes: VerifyOutcome list
    | Abandoned of reason: string

type TaskState =
    /// Waiting for the next turn. `Ready {0;0}` is the queued state.
    | Ready of Progress
    | Running of Progress * ModelTier
    | Blocked of Progress * NeedsDecision
    | Verifying of Progress
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
    | VerifyCompleted of VerifyOutcome list
    | Abandon of reason: string

type TransitionError = { State: string; Event: string }

[<RequireQualifiedAccess>]
module TaskState =

    let queued = Ready { Turns = 0; VerifyAttempts = 0 }

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
        | Abandon _ -> "Abandon"

    /// The whole task lifecycle. Budgets are enforced here, not by agents or prompts.
    ///
    /// TODO(you) — RESOURCES.md §2. Spec: every test starting "state:".
    /// Transition table (anything not listed is illegal -> Error { State; Event }):
    ///   Passed | Failed          + anything              -> illegal (terminal)
    ///   any non-terminal         + Abandon r             -> Failed(Abandoned r, progress)
    ///   Ready p                  + TurnStarted tier      -> Failed(TurnBudgetExhausted) if p.Turns >= MaxTurns
    ///                                                       else Running({p with Turns+1}, tier)
    ///   Running(p,_)             + TurnEnded             -> Ready p
    ///   Running(p,_) | Ready p   + NeedsDecisionRaised n -> Blocked(p, n)
    ///   Blocked(p,_)             + DecisionBound _       -> Ready p
    ///   Ready p                  + VerifyRequested       -> Failed(VerifyBudgetExhausted []) if p.VerifyAttempts >= Max
    ///                                                       else Verifying {p with VerifyAttempts+1}
    ///   Verifying p              + VerifyCompleted os    -> Passed ev   if PassedEvidence.Create succeeds
    ///                                                    -> Failed(VerifyBudgetExhausted os) if attempts used up
    ///                                                    -> Ready p     otherwise (agent gets another turn)
    /// Use one `match state, event with` and let FS0025 (incomplete match = error) keep you honest.
    let transition (limits: Limits) (state: TaskState) (event: TaskEvent) : Result<TaskState, TransitionError> =
        ignore (limits, state, event, name, eventName)
        failwith "TODO: TaskState.transition"

    /// Rebuild a task's state from its ledger events. This is how a restarted runner resumes.
    let replay (limits: Limits) (events: TaskEvent seq) : Result<TaskState, TransitionError> =
        events
        |> Seq.fold (fun acc e -> acc |> Result.bind (fun s -> transition limits s e)) (Ok queued)
