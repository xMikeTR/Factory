module Factory.Core.Tests.TraceTests

open Factory.Core
open Factory.Core.Tests.Support

let private progress: Progress = { Turns = 3; VerifyAttempts = 1; ReviewRounds = 0; OpenFindings = [] }

let private nd source =
    { Question = "q"; Elements = []; Source = source; Options = []; Recommended = None }

let private turn model tin tout calls = { ModelId = model; TokensIn = tin; TokensOut = tout; ToolCalls = calls }

let private input endState =
    { Run = "R1"; Task = "T-001"; End = endState; LastDecisionEscalated = false
      Turns = [ turn "claude-a" 100L 10L 4; turn "gpt-b" 50L 5L 2; turn "claude-a" 20L 2L 1 ]
      VerifyAttempts = 2; ReviewRounds = 1; ScopeReverts = 0
      SharedTokensIn = 7L; SharedTokensOut = 3L
      BundleId = None; TraceId = Some "abc"; Jira = Some "PRJ-1"; Procedures = [] }

let tests: (string * (unit -> unit)) list =
    [ "trace: a task still in flight has no end and no trace", fun () ->
          equal None (TaskEnd.classify false InFlight) "in flight"
          equal InFlight (EndState.ofTaskState (Ready(progress, TestAuthor))) "ready"
          equal None (Trace.build (input InFlight)) "no trace"
      "trace: passed has no cause", fun () ->
          equal (Some { Outcome = TracePassed; Cause = None }) (TaskEnd.classify false EndPassed) "passed"
      "trace: an abandon right after an escalate is a stall; any other abandon is abandoned", fun () ->
          let ended = EndFailed(Abandoned "not making progress (2 turns with no change)")
          equal (Some { Outcome = TraceFailed; Cause = Some TraceCause.Stalled }) (TaskEnd.classify true ended) "stalled"
          equal (Some { Outcome = TraceFailed; Cause = Some TraceCause.Abandoned }) (TaskEnd.classify false ended) "abandoned"
      "trace: exhausted budgets are failed gates", fun () ->
          equal (Some { Outcome = TraceFailed; Cause = Some(TraceCause.GateFailed "turn_budget") }) (TaskEnd.classify false (EndFailed(TurnBudgetExhausted 20))) "turns"
          equal (Some { Outcome = TraceFailed; Cause = Some(TraceCause.GateFailed "verify") }) (TaskEnd.classify false (EndFailed(VerifyBudgetExhausted []))) "verify"
      "trace: an agent's question parks the task; a harness check that parks it is a failed gate", fun () ->
          equal (Some { Outcome = TraceBlocked; Cause = Some TraceCause.Parked }) (TaskEnd.classify false (EndBlocked "agent")) "agent"
          equal (Some { Outcome = TraceBlocked; Cause = Some(TraceCause.GateFailed "environment") }) (TaskEnd.classify false (EndBlocked "environment")) "environment"
          equal (Some { Outcome = TraceBlocked; Cause = Some(TraceCause.GateFailed "ui") }) (TaskEnd.classify false (EndBlocked "ui")) "ui"
      "trace: task states map to end states", fun () ->
          equal (EndBlocked "ui") (EndState.ofTaskState (Blocked(progress, TestAuthor, nd "ui"))) "blocked"
          equal (EndFailed(TurnBudgetExhausted 9)) (EndState.ofTaskState (Failed(TurnBudgetExhausted 9, progress))) "failed"
      "trace: the ledger spelling of a cause; a gate name is kept to [a-z0-9_.-]", fun () ->
          equal "stalled" (TraceCause.toLedger TraceCause.Stalled) "stalled"
          equal "parked" (TraceCause.toLedger TraceCause.Parked) "parked"
          equal "abandoned" (TraceCause.toLedger TraceCause.Abandoned) "abandoned"
          equal "gate_failed:turn_budget" (TraceCause.toLedger (TraceCause.GateFailed "turn_budget")) "gate"
          equal "gate_failed:ui_guard" (TraceCause.toLedger (TraceCause.GateFailed "UI guard")) "normalised"
          equal "gate_failed:unknown" (TraceCause.toLedger (TraceCause.GateFailed "  ")) "empty"
      "trace: models in first-use order, tokens are the turns plus the task's share of the planner", fun () ->
          match Trace.build (input EndPassed) with
          | None -> failwith "expected a trace"
          | Some t ->
              equal [ "claude-a"; "gpt-b" ] t.Models "models"
              equal 177L t.TokensIn "tokens in"
              equal 20L t.TokensOut "tokens out"
              equal ({ Turns = 3; ToolCalls = 7; VerifyAttempts = 2; ReviewRounds = 1; ScopeReverts = 0 }: TraceActions) t.Actions "actions"
              equal (Some { Outcome = TracePassed; Cause = None }) (Some t.End) "end"
      "trace: no bundle is explicit, not missing", fun () ->
          match Trace.build (input EndPassed) with
          | Some t -> equal None t.BundleId "no bundle"
          | None -> failwith "expected a trace" ]
