namespace Factory.Core

/// How a task ended, in the shape designc's `trace` row and `task.cause` take (System 3 learns from these).
/// No model weights are involved: a trace is (task, context given, what was done, outcome) for token-space learning.
type TraceOutcome =
    | TracePassed
    | TraceFailed
    | TraceBlocked

/// Why a task did not pass, from what the harness saw (never from the agent's own account).
/// Ledger pairs: Stalled and Abandoned -> failed, Parked -> blocked, GateFailed -> failed or blocked.
[<RequireQualifiedAccess>]
type TraceCause =
    /// The configurator saw no progress and escalated; the task was abandoned for it.
    | Stalled
    /// The agent asked a designer a question and waits on the answer.
    | Parked
    /// A designer abandoned or rejected the task.
    | Abandoned
    /// A harness check stopped the task: a budget, verify, or a check that parks it (environment, ui, review).
    | GateFailed of gate: string

[<RequireQualifiedAccess>]
module TraceCause =
    /// A gate name as designc's schema allows it: [a-z0-9_.-]+.
    let private gateName (g: string) =
        let cleaned =
            g.Trim().ToLowerInvariant()
            |> String.map (fun c -> if System.Char.IsAsciiLetterLower c || System.Char.IsAsciiDigit c || c = '_' || c = '.' || c = '-' then c else '_')
        if cleaned = "" then "unknown" else cleaned

    let toLedger =
        function
        | TraceCause.Stalled -> "stalled"
        | TraceCause.Parked -> "parked"
        | TraceCause.Abandoned -> "abandoned"
        | TraceCause.GateFailed g -> "gate_failed:" + gateName g

type TaskEnd = { Outcome: TraceOutcome; Cause: TraceCause option }

/// The part of a TaskState the trace needs. Kept apart from TaskState so the classification is tested
/// without building Passed (whose evidence only verify can construct).
type EndState =
    | InFlight
    | EndPassed
    | EndFailed of FailureReason
    /// The needs-decision's source: "agent" for the agent's own question, else the harness check that parked it.
    | EndBlocked of source: string

[<RequireQualifiedAccess>]
module EndState =
    let ofTaskState =
        function
        | Passed _ -> EndPassed
        | Failed(reason, _) -> EndFailed reason
        | Blocked(_, _, nd) -> EndBlocked nd.Source
        // Parked after the review rounds ran out: waiting on a designer, so not in flight.
        | Reviewing(_, _, _, Some _) -> EndBlocked "review"
        | Ready _
        | Running _
        | Verifying _
        | Reviewing(_, _, _, None) -> InFlight

[<RequireQualifiedAccess>]
module TaskEnd =
    /// `lastDecisionEscalated`: the task's latest configurator decision was Escalate. An escalation always
    /// abandons the task at once, so an abandon after one is the stall and any other abandon is a person's.
    let classify (lastDecisionEscalated: bool) (state: EndState) : TaskEnd option =
        let ended outcome cause = Some { Outcome = outcome; Cause = cause }
        match state with
        | InFlight -> None
        | EndPassed -> ended TracePassed None
        | EndFailed(Abandoned _) when lastDecisionEscalated -> ended TraceFailed (Some TraceCause.Stalled)
        | EndFailed(Abandoned _) -> ended TraceFailed (Some TraceCause.Abandoned)
        | EndFailed(TurnBudgetExhausted _) -> ended TraceFailed (Some(TraceCause.GateFailed "turn_budget"))
        | EndFailed(VerifyBudgetExhausted _) -> ended TraceFailed (Some(TraceCause.GateFailed "verify"))
        | EndBlocked "agent" -> ended TraceBlocked (Some TraceCause.Parked)
        | EndBlocked source -> ended TraceBlocked (Some(TraceCause.GateFailed source))

/// A versioned procedure that ran: a prompt, a tool policy or a gate config. Hash is the identity.
type ProcedureRef = { Id: string; Version: string; Hash: string }

/// One agent turn of the task, as the run ledger's `turn` row records it.
type TraceTurn =
    { ModelId: string
      TokensIn: int64
      TokensOut: int64
      ToolCalls: int }

type TraceActions =
    { Turns: int
      ToolCalls: int
      VerifyAttempts: int
      ReviewRounds: int
      ScopeReverts: int }

/// What the harness reads from the run ledger for one task.
type TraceInput =
    { Run: string
      Task: string
      End: EndState
      LastDecisionEscalated: bool
      Turns: TraceTurn list
      VerifyAttempts: int
      ReviewRounds: int
      ScopeReverts: int
      /// The task's share of run-level tokens (the planner's), as the projected `task` row counts them.
      SharedTokensIn: int64
      SharedTokensOut: int64
      /// None: the run fell back without a context bundle.
      BundleId: string option
      TraceId: string option
      Jira: string option
      Procedures: ProcedureRef list }

/// One task's trace. Cost is not here: S2 records tokens per model and the value view prices them.
type TaskTrace =
    { Run: string
      Task: string
      End: TaskEnd
      BundleId: string option
      TraceId: string option
      Jira: string option
      Models: string list
      Procedures: ProcedureRef list
      Actions: TraceActions
      TokensIn: int64
      TokensOut: int64 }

[<RequireQualifiedAccess>]
module Trace =
    /// None while the task is still in flight: a trace is written when the task ends (a blocked end is not final).
    let build (i: TraceInput) : TaskTrace option =
        TaskEnd.classify i.LastDecisionEscalated i.End
        |> Option.map (fun ended ->
            { Run = i.Run
              Task = i.Task
              End = ended
              BundleId = i.BundleId
              TraceId = i.TraceId
              Jira = i.Jira
              Models = i.Turns |> List.map (fun t -> t.ModelId) |> List.distinct
              Procedures = i.Procedures
              Actions =
                { Turns = i.Turns.Length
                  ToolCalls = i.Turns |> List.sumBy (fun t -> t.ToolCalls)
                  VerifyAttempts = i.VerifyAttempts
                  ReviewRounds = i.ReviewRounds
                  ScopeReverts = i.ScopeReverts }
              TokensIn = (i.Turns |> List.sumBy (fun t -> t.TokensIn)) + i.SharedTokensIn
              TokensOut = (i.Turns |> List.sumBy (fun t -> t.TokensOut)) + i.SharedTokensOut })
