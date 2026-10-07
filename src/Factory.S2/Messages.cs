namespace Factory.S2;

// Messages carry ids and paths, never domain objects: they must serialise into MAF checkpoints,
// and every executor re-reads and re-validates what it needs (see FactoryServices).

/// Entry point: run System 2 on one frozen handover package. Since: a previous run of an earlier
/// freeze of this handover; its plan is reused and only the tasks a changed decision touches re-run.
public sealed record StartRun(string RunId, string HandoverPath, string? Since = null);

/// Planner -> validator: a plan.json has been written for validation.
public sealed record PlanDraft(string RunId, int Attempt);

/// Validator -> planner: fix these and try again. Errors are PlanError descriptions.
public sealed record PlanRejected(string RunId, int Attempt, string[] Errors);

/// Validator -> dispatcher.
public sealed record PlanAccepted(string RunId);

/// Dispatcher -> wave executor: run this wave.
public sealed record WaveReady(string RunId, int WaveIndex, DecisionAnswer[] Answers);

/// Wave executor -> dispatcher: all teams in the wave passed and merged into integration.
public sealed record WaveCompleted(string RunId, int WaveIndex);

/// Dispatcher -> integration verify: every wave done.
public sealed record IntegrationReady(string RunId);

/// Integration verify -> merge.
public sealed record IntegrationVerified(string RunId);

/// Terminal message for Finalize, from any stage. Status "parked": Detail is the open ND ids.
public sealed record RunFinished(string RunId, string Status, string Detail);

/// What the CLI prints when a run ends.
public static class RunReport
{
    public static string Line(RunFinished r) => r.Status == "parked" ? $"parked: {r.Detail}" : $"{r.Status}\n{r.Detail}";
}

// ---------- needs-decision (registry) and merge approval (RequestPort) ----------

public sealed record BlockedTask(string TeamId, string TaskId, string Question, string Source, string[] Elements,
    OfferedOption[] Options, string? Recommended);

/// One option of a blocked task's question, as the agent offered it.
public sealed record OfferedOption(string Id, string Summary, string Consequence);

/// One answer per blocked task: a newly bound decision id, or null to abandon the task.
public sealed record DecisionAnswer(string TaskId, string? DecisionId, string? Note);

/// Merge executor -> "merge-approval" port, for tiers that need a human.
public sealed record MergeApprovalRequest(string RunId, string PullRequestUrl, string Tier, string[] Reasons);

public sealed record MergeApprovalResponse(string RunId, bool Merged, string? ApprovedBy);
