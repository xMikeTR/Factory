using Factory.S2.Executors;
using Microsoft.Agents.AI.Workflows;

namespace Factory.S2;

/// The System 2 outer workflow:
///
///   Planner ⇄ PlanValidator → Dispatcher ⇄ Waves ⇄ [needs-decision]
///                                 ↓
///                        IntegrationVerify → Merge ⇄ [merge-approval]
///   (Planner | Waves | IntegrationVerify | Merge) → Finalize
///
/// Only the planner and the team loop inside Waves call models; every other node is deterministic.
public static class S2Workflow
{
    public static Workflow Build(FactoryServices svc)
    {
        var planner = new PlannerExecutor(svc);
        var validator = new PlanValidatorExecutor(svc);
        var dispatcher = new DispatchExecutor(svc);
        var waves = new WaveExecutor(svc);
        var integration = new IntegrationVerifyExecutor(svc);
        var merge = new MergeExecutor(svc);
        var finalize = new FinalizeExecutor(svc);

        var needsDecision = RequestPort.Create<NeedsDecisionRequest, NeedsDecisionResponse>("needs-decision");
        var mergeApproval = RequestPort.Create<MergeApprovalRequest, MergeApprovalResponse>("merge-approval");

        // TODO(you) — RESOURCES.md §8. Wire the graph. Each edge carries one message type:
        //   planner     -> validator (PlanDraft), finalize (RunFinished)
        //   validator   -> planner (PlanRejected), dispatcher (PlanAccepted)
        //   dispatcher  -> waves (WaveReady), integration (IntegrationReady)
        //   waves       -> dispatcher (WaveCompleted), needsDecision (NeedsDecisionRequest), finalize (RunFinished)
        //   needsDecision -> waves            (responses come back to the requester)
        //   integration -> merge (IntegrationVerified), finalize (RunFinished)
        //   merge       -> mergeApproval (MergeApprovalRequest), finalize (RunFinished)
        //   mergeApproval -> merge
        //   output from finalize
        // Start: new WorkflowBuilder(planner).AddEdge<object>(planner, validator, When<PlanDraft>)...
        // Check against your MAF version: conditional AddEdge overload, WithOutputFrom, Build.
        _ = (validator, dispatcher, waves, integration, merge, finalize, needsDecision, mergeApproval);
        throw new NotImplementedException("TODO: S2Workflow.Build (RESOURCES.md §8)");
    }

    private static bool When<T>(object? message) => message is T;
}

/// Runs the workflow and answers RequestPort requests. This file and S2Workflow.Build are the only
/// places that touch MAF's run-time API, so a MAF upgrade is a two-file change.
///
/// Resume: every executor is idempotent against the run directory, git and the ledger, so
/// re-running `factory-s2 run --run-id <same id>` picks up where it stopped (plan reused, finished
/// tasks replay to Passed, merges are no-ops). MAF checkpointing (CheckpointManager) can be layered
/// on to also restore in-flight requests without re-deriving them; add it once the spike confirms
/// the checkpoint API for your version (see the open decision on the checkpoint store).
public static class WorkflowHost
{
    public static async Task<RunFinished?> RunAsync(Workflow workflow, StartRun start, IHumanChannel human, CancellationToken ct)
    {
        await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, start, cancellationToken: ct);

        await foreach (WorkflowEvent evt in run.WatchStreamAsync(ct))
        {
            switch (evt)
            {
                case RequestInfoEvent request:
                    if (request.Request.TryGetDataAs<NeedsDecisionRequest>(out var nd))
                        await run.SendResponseAsync(request.Request.CreateResponse(await human.AskAsync(nd, ct)));
                    else if (request.Request.TryGetDataAs<MergeApprovalRequest>(out var ma))
                        await run.SendResponseAsync(request.Request.CreateResponse(await human.ApproveAsync(ma, ct)));
                    break;

                case WorkflowOutputEvent output when output.Data is RunFinished finished:
                    return finished;

                case ExecutorFailedEvent failed:
                    Console.Error.WriteLine($"[{failed.ExecutorId}] failed: {failed.Data}");
                    break;

                case WorkflowErrorEvent error:
                    Console.Error.WriteLine($"workflow error: {error.Data}");
                    return new RunFinished(start.RunId, "error", error.Data?.ToString() ?? "unknown");
            }
        }
        return null;
    }
}
