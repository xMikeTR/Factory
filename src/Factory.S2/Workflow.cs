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

        // One typed edge per message: an executor's SendsMessage list and these edges must agree,
        // and a message with no edge is a build-time error in MAF rather than a silent drop.
        return new WorkflowBuilder(planner)
            .ForwardMessage<PlanDraft>(planner, validator)
            .ForwardMessage<RunFinished>(planner, finalize)
            .ForwardMessage<PlanRejected>(validator, planner)
            .ForwardMessage<PlanAccepted>(validator, dispatcher)
            .ForwardMessage<WaveReady>(dispatcher, waves)
            .ForwardMessage<IntegrationReady>(dispatcher, integration)
            .ForwardMessage<WaveCompleted>(waves, dispatcher)
            .ForwardMessage<NeedsDecisionRequest>(waves, needsDecision)
            .ForwardMessage<RunFinished>(waves, finalize)
            .ForwardMessage<NeedsDecisionResponse>(needsDecision, waves)
            .ForwardMessage<IntegrationVerified>(integration, merge)
            .ForwardMessage<RunFinished>(integration, finalize)
            .ForwardMessage<MergeApprovalRequest>(merge, mergeApproval)
            .ForwardMessage<RunFinished>(merge, finalize)
            .ForwardMessage<MergeApprovalResponse>(mergeApproval, merge)
            .WithOutputFrom(finalize)
            .Build();
    }

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
