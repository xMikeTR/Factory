namespace Factory.Core

type Limits = { MaxTurns: int; MaxVerifyAttempts: int }

/// ReviewRounds: blocking reviews so far (review turns are not Turns); OpenFindings: what the last
/// blocking review found, handed to the fix turn and to the re-review.
type Progress =
    { Turns: int
      VerifyAttempts: int
      ReviewRounds: int
      OpenFindings: Finding list }

/// One answer a designer can pick, by id ("A", "B"), instead of rewording prose.
type DecisionOption =
    { Id: string
      Summary: string
      /// What follows if this option is chosen.
      Consequence: string }

type NeedsDecision =
    { Question: string
      Elements: ElementId list
      /// Why the task stopped: the agent asked, or the configurator saw a stall.
      Source: string
      /// What the designer chooses from. An agent's question always has at least two;
      /// one the factory raises itself (environment, stall) may have none.
      Options: DecisionOption list
      Recommended: string option }

[<RequireQualifiedAccess>]
module NeedsDecision =
    let private idOk (id: string) = id.Length = 1 && System.Char.IsAsciiLetterUpper id.[0]

    /// Why these options are not a choice a designer can make, in words for the agent; empty when they are.
    let optionErrors (options: DecisionOption list) (recommended: string option) : string list =
        let ids = options |> List.map (fun o -> o.Id)
        [ if options.Length < 2 then "give at least two options: a question with one answer is not a decision"
          for o in options do
              if not (idOk o.Id) then $"option id '{o.Id}' must be one capital letter (A, B, C...)"
              if System.String.IsNullOrWhiteSpace o.Summary then $"option {o.Id} needs a summary"
              if System.String.IsNullOrWhiteSpace o.Consequence then $"option {o.Id} needs a consequence"
          for d in ids |> List.countBy id |> List.filter (fun (_, n) -> n > 1) |> List.map fst do
              $"option id {d} is used twice"
          let known = String.concat ", " ids
          match recommended with
          | Some r when not (List.contains r ids) -> $"recommended '{r}' is not one of the options ({known})"
          | _ -> () ]

    /// The answer the agent gets back: the chosen option in full, then the designer's note.
    /// A resolution naming no option falls back to the note (designc may not record options yet).
    let answer (nd: NeedsDecision) (option: string option) (note: string option) : Result<string, string> =
        let withNote (text: string) = match note with Some n when n.Trim() <> "" -> $"{text}\nNote: {n}" | _ -> text
        match option with
        | Some id ->
            match nd.Options |> List.tryFind (fun o -> o.Id = id) with
            | Some o -> Ok(withNote $"Chosen option {o.Id}: {o.Summary}. Consequence: {o.Consequence}")
            | None ->
                let ids = nd.Options |> List.map (fun o -> o.Id) |> String.concat ", "
                Error $"option '{id}' is not one of the options ({ids})"
        | None -> Ok(defaultArg note "resolved in the registry; re-read the handover and decisions")

type VerifyOutcome =
    { Name: string
      ExitCode: int
      DurationMs: int64
      OutputTail: string }

/// What a check run reports when the restore gate (ProjectFileChange) refused the run's changes to
/// the files `dotnet restore` reads: no check ran, so it is neither red nor a failing stash check.
[<RequireQualifiedAccess>]
module RestoreRefusal =
    let name = "restore refused"

    let outcome (reasons: string list) : VerifyOutcome =
        { Name = name; ExitCode = 1; DurationMs = 0L; OutputTail = String.concat "\n" reasons }

    let isRefusal (o: VerifyOutcome) = o.Name = name

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

/// What a red run shows, read before it becomes evidence.
type RedReading =
    | Red of RedKind
    | Unrunnable of checks: string list
    | Refused of reasons: string

/// Reads the TestAuthor's red run before it becomes evidence.
[<RequireQualifiedAccess>]
module RedRun =

    let private matches (pattern: string) (text: string) =
        System.Text.RegularExpressions.Regex.IsMatch(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)

    /// The sandbox could not run the check: a package source it cannot reach (verify runs with
    /// the network off), or a command the image does not have. No code change fixes either.
    let private environmental (o: VerifyOutcome) =
        o.ExitCode = 127 || matches @"error NU1301\b" o.OutputTail

    /// Refused: the restore gate refused the author's project-file changes, so nothing ran; the
    /// author fixes them. Unrunnable: the checks the sandbox could not run. One is enough: it can
    /// never go green, so the Implementer would spend every turn against it. Red: the kind of red
    /// (a compile failure proves less than a failing assertion); RedEvidence.Create still refuses
    /// a run with no red.
    let classify (outcomes: VerifyOutcome list) : RedReading =
        let failed = outcomes |> List.filter (fun o -> o.ExitCode <> 0)
        match outcomes |> List.tryFind RestoreRefusal.isRefusal, failed |> List.filter environmental with
        | Some refused, _ -> Refused refused.OutputTail
        | None, [] when failed |> List.exists (fun o -> matches @"error (CS|FS)\d+|Build FAILED" o.OutputTail) -> Red BuildFailure
        | None, [] -> Red AssertionFailure
        | None, env -> Unrunnable(env |> List.map (fun o -> o.Name))

/// Who holds the pen. The Implementer phase cannot exist without the red evidence that ended
/// the TestAuthor phase.
type Phase =
    | TestAuthor
    | Implementer of RedEvidence

/// Proof that a task's checks hold. Three gates: the tests were red before implementation, every
/// automated check is green now, and with the implementation stashed at least one check fails
/// again (so the tests exercise the change rather than pass vacuously). The fourth gate, the
/// review, is what turns this into a pass.
[<Sealed>]
type VerifiedEvidence private (attempt: int, green: VerifyOutcome list, red: RedEvidence, stashedImpl: VerifyOutcome list) =
    member _.Attempt = attempt
    member _.Outcomes = green
    member _.Red = red
    member _.StashedImpl = stashedImpl

    static member Create(attempt: int, green: VerifyOutcome list, red: RedEvidence, stashedImpl: VerifyOutcome list) : Result<VerifiedEvidence, string> =
        match green with
        | [] -> Error "no verify outcomes"
        | os when os |> List.exists (fun o -> o.ExitCode <> 0) ->
            let failed = os |> List.filter (fun o -> o.ExitCode <> 0) |> List.map (fun o -> o.Name)
            Error("failed: " + System.String.Join(", ", failed))
        | _ when stashedImpl |> List.forall (fun o -> o.ExitCode = 0 || RestoreRefusal.isRefusal o) ->
            Error "stashed-impl check: with the implementation stashed every test still passes, so the tests do not exercise it"
        | _ -> Ok(VerifiedEvidence(attempt, green, red, stashedImpl))

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
    /// Verified, awaiting review. With `Some nd` it is parked for a designer: the review rounds ran
    /// out (`ReviewRounds >= maxReviewRounds`, `OpenFindings` still blocking) or no usable review came back.
    | Reviewing of Progress * Phase * VerifiedEvidence * NeedsDecision option
    | Passed of VerifiedEvidence * ReviewEvidence
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
    /// One review round of a verified task. Blocking findings send it back, none passes it.
    | ReviewCompleted of ReviewResult
    /// A designer accepts the open findings at the round limit; they go on the PR.
    | ReviewAccepted of note: string * Independence
    /// A designer sends the task back with guidance, for another set of review rounds.
    | ReviewGuided of guidance: string
    | Abandon of reason: string

type TransitionError = { State: string; Event: string }

[<RequireQualifiedAccess>]
module TaskState =

    /// Blocking review rounds a task gets before it parks for a designer.
    let maxReviewRounds = 3

    let queued = Ready({ Turns = 0; VerifyAttempts = 0; ReviewRounds = 0; OpenFindings = [] }, TestAuthor)

    let private name (s: TaskState) =
        match s with
        | Ready _ -> "Ready"
        | Running _ -> "Running"
        | Blocked _ -> "Blocked"
        | Verifying _ -> "Verifying"
        | Reviewing _ -> "Reviewing"
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
        | ReviewCompleted _ -> "ReviewCompleted"
        | ReviewAccepted _ -> "ReviewAccepted"
        | ReviewGuided _ -> "ReviewGuided"
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
    ///   Verifying(p,ph)            + NeedsDecisionRaised n -> Blocked(p, ph, n) (a check timed out twice)
    ///   Verifying(p,Implementer r) + VerifyCompleted(g,s)  -> Reviewing(p, ph, ev) if VerifiedEvidence.Create succeeds
    ///                                                      -> Failed(VerifyBudgetExhausted) if attempts used up
    ///                                                      -> Ready(p, TestAuthor) if green but the stash check failed
    ///                                                      -> Ready(p, Implementer r) otherwise
    ///   Reviewing(p,ph,ev,None)    + ReviewCompleted r     -> Passed(ev, review) if nothing blocks; else ReviewRounds+1 and
    ///                                                         Ready(p, TestAuthor) for weak tests, Ready(p, ph) otherwise,
    ///                                                         or, at maxReviewRounds, Reviewing awaiting a ND (not a turn)
    ///   Reviewing(p,None)          + NeedsDecisionRaised n -> Reviewing(.., Some n) (rounds ran out, or no usable review)
    ///   Reviewing(.., Some _)      + ReviewAccepted        -> Passed(ev, open findings accepted)
    ///   Reviewing(.., Some _)      + ReviewGuided          -> Ready(p, ph); ReviewRounds reset only if they had run out
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
            | VerifyCompleted _
            | ReviewCompleted _
            | ReviewAccepted _
            | ReviewGuided _ -> illegal ()
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
            | ReturnedToTestAuthor _
            | ReviewCompleted _
            | ReviewAccepted _
            | ReviewGuided _ -> illegal ()
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
            | ReturnedToTestAuthor _
            | ReviewCompleted _
            | ReviewAccepted _
            | ReviewGuided _ -> illegal ()
        | Verifying(p, ph) ->
            match event with
            | Abandon r -> Ok(Failed(Abandoned r, p))
            | VerifyCompleted(green, stashed) ->
                match ph with
                | TestAuthor -> illegal ()
                | Implementer red ->
                    let greenOk = not green.IsEmpty && green |> List.forall (fun o -> o.ExitCode = 0)
                    match VerifiedEvidence.Create(p.VerifyAttempts, green, red, stashed) with
                    | Ok ev -> Ok(Reviewing(p, ph, ev, None))
                    | Error _ when p.VerifyAttempts >= limits.MaxVerifyAttempts ->
                        Ok(Failed(VerifyBudgetExhausted(if greenOk then stashed else green), p))
                    | Error _ when greenOk -> Ok(Ready(p, TestAuthor))
                    | Error _ -> Ok(Ready(p, ph))
            | NeedsDecisionRaised n -> Ok(Blocked(p, ph, n)) // a check that could not answer (timed out twice) is not evidence either way
            | TurnStarted _
            | TurnEnded
            | DecisionBound _
            | VerifyRequested
            | TestsAuthored _
            | ReturnedToTestAuthor _
            | ReviewCompleted _
            | ReviewAccepted _
            | ReviewGuided _ -> illegal ()
        | Reviewing(p, ph, ev, parked) ->
            let exhausted = p.ReviewRounds >= maxReviewRounds
            let passed (r: ReviewEvidence) = Ok(Passed(ev, r))
            match event, parked with
            | Abandon r, _ -> Ok(Failed(Abandoned r, p))
            | ReviewCompleted r, None when not exhausted ->
                let rounds = p.ReviewRounds + 1
                match r.Blocking with
                | [] -> passed { Advisory = r.Advisory; Accepted = []; Independence = r.Independence; Rounds = rounds; Note = None }
                | blocking ->
                    let p' = { p with ReviewRounds = rounds; OpenFindings = blocking }
                    if rounds >= maxReviewRounds then Ok(Reviewing(p', ph, ev, None)) // a designer decides next
                    else
                        match Review.route blocking with
                        | ToTestAuthor -> Ok(Ready(p', TestAuthor))
                        | ToImplementer -> Ok(Ready(p', ph))
            | NeedsDecisionRaised n, None -> Ok(Reviewing(p, ph, ev, Some n)) // rounds ran out, or no usable review could be had
            | ReviewAccepted(note, independence), Some _ ->
                passed { Advisory = []; Accepted = p.OpenFindings; Independence = independence; Rounds = p.ReviewRounds; Note = Some note }
            | ReviewGuided _, Some _ -> Ok(Ready((if exhausted then { p with ReviewRounds = 0 } else p), ph)) // guidance buys fresh rounds only once they ran out
            | (ReviewCompleted _ | NeedsDecisionRaised _ | ReviewAccepted _ | ReviewGuided _), _
            | (TurnStarted _ | TurnEnded | DecisionBound _ | VerifyRequested | VerifyCompleted _ | TestsAuthored _ | ReturnedToTestAuthor _), _ -> illegal ()

    /// Rebuild a task's state from its ledger events. This is how a restarted runner resumes.
    let replay (limits: Limits) (events: TaskEvent seq) : Result<TaskState, TransitionError> =
        events
        |> Seq.fold (fun acc e -> acc |> Result.bind (fun s -> transition limits s e)) (Ok queued)
