using System.Text.Json;
using Factory.Core;
using Factory.Harness;

namespace Factory.S2.Teams;

public enum TeamStatus { Passed, Blocked, Failed }

public sealed record TeamResult(string TeamId, TeamStatus Status, BlockedTask? Blocked = null, string? Failure = null);

/// Runs one team's tasks, in order, in the team's worktree.
///
/// Deliberately a plain loop over the F# state machine rather than a nested MAF workflow: the
/// number of teams is only known after dispatch, teams in a wave must not wait on each other
/// (MAF runs executors in supersteps with a barrier between them), and every transition is
/// already persisted to the ledger, so a restarted runner resumes by replaying it.
public sealed class TeamRunner(FactoryServices svc, Handover handover, string runId)
{
    public async Task<TeamResult> RunAsync(Team team, IReadOnlyDictionary<string, DecisionAnswer> answers, CancellationToken ct)
    {
        var teamId = team.Id.Value;
        var worktree = await svc.Git.EnsureTeamAsync(runId, teamId, ct);

        foreach (var task in team.Tasks)
        {
            var stop = await new TaskLoop(svc, handover, runId, team, task, worktree).RunAsync(answers, ct);
            if (stop is not null) return stop;
        }

        await svc.Git.MergeTeamAsync(runId, teamId, ct);
        await svc.LogAsync(runId, "team", LedgerKinds.Dispatch, outcome: "merged_to_integration", teamId: teamId, ct: ct);
        return new TeamResult(teamId, TeamStatus.Passed);
    }
}

/// One task from its current state to Passed, Blocked or Failed.
internal sealed class TaskLoop(FactoryServices svc, Handover handover, string runId, Team team, PlannedTask task, string worktree)
{
    private readonly string _teamId = team.Id.Value;
    private readonly string _taskId = task.Id.Value;
    private readonly Limits _limits = svc.Settings.TaskLimits;

    private string? _approachNote;
    private string? _lastFailures;
    private string? _resolvedDecision;
    private bool _doneDeclared;
    private bool _lastVerifyFailed;
    private int _turnsWithoutChange;

    /// Drive the task from whatever state the ledger says it is in to Passed (return null),
    /// Blocked or Failed (return a TeamResult). This is the harness loop: it is yours to write.
    ///
    /// TODO(you) — RESOURCES.md §6. Suggested shape:
    ///   state = Core.Replay(limits, ledger payloads for this task)       // resume for free
    ///   if replayed state is Passed: commit leftover changes only if ALL are inside task.Writes; return null
    ///   _approachNote = await LastApproachNoteAsync(ct)
    ///   loop, switching on state:
    ///     Passed     -> EnforceScopeAsync, CommitAsync, return null
    ///     Failed f   -> return TeamResult(Failed, f.Item1)
    ///     Running    -> (crashed mid-turn) ApplyAsync(TurnEnded)
    ///     Blocked b  -> if an answer exists for this task (use it once): DecisionBound or Abandon;
    ///                   else return TeamResult(Blocked, BlockedTask from b.Item2)
    ///     Verifying v-> outcomes = svc.Verify.RunTaskAsync(..., v.Item.VerifyAttempts, ...)
    ///                   ApplyAsync(VerifyCompleted); if back to Ready, keep Briefs.Failures(outcomes)
    ///     Ready r    -> NextTurnAsync
    /// Every state change goes through ApplyAsync, which writes the ledger row first. Never
    /// assign `state` any other way, or replay and reality drift apart.
    public async Task<TeamResult?> RunAsync(IReadOnlyDictionary<string, DecisionAnswer> answers, CancellationToken ct)
    {
        await Task.CompletedTask;
        throw new NotImplementedException("TODO: TaskLoop.RunAsync (RESOURCES.md §6)");
    }

    /// One turn from Ready.
    ///
    /// TODO(you) — RESOURCES.md §6.
    ///   if _doneDeclared: EnforceScopeAsync, then ApplyAsync(VerifyRequested)
    ///   decision = svc.Configurator.DecideAsync(TurnContext from progress + fields); log it
    ///   Escalate -> ApplyAsync(NeedsDecisionRaised, source "configurator")
    ///   Plan     -> TurnStarted; task-planner agent with ws.ReadOnlyTools(); log turn (tokens);
    ///               needs-decision signal? raise it : store response.Text as _approachNote; TurnEnded
    ///   Act      -> fingerprint before; TurnStarted; executor agent with ws.ExecutorTools(); log turn;
    ///               EnforceScopeAsync (tell the agent what was reverted next turn); fingerprint after
    ///               -> _turnsWithoutChange; needs-decision signal? raise it : _doneDeclared = signals.DoneSummary
    ///               set and nothing reverted; TurnEnded
    /// Agents: svc.Agents.Create(new AgentScope(runId, Roles.X, _teamId, _taskId), tier,
    ///         Briefs.Prompt("executor"|"task-planner"), tools); brief = Briefs.Task(...).
    private Task<TaskState> NextTurnAsync(TaskState state, Progress progress, CancellationToken ct) =>
        throw new NotImplementedException("TODO: TaskLoop.NextTurnAsync (RESOURCES.md §6)");

    /// Deterministic backstop to the workspace tools: anything changed outside the task's writes
    /// (e.g. by a build command) is reverted and logged. Returns the reverted paths.
    private async Task<List<string>> EnforceScopeAsync(string when, CancellationToken ct)
    {
        var changed = await svc.Git.ChangedPathsAsync(worktree, ct);
        var outside = changed
            .Where(p => handover.ProtectedPaths.Any(pp => Core.Matches(pp, p)) || !task.Writes.Any(w => Core.Matches(w, p)))
            .ToList();
        if (outside.Count == 0) return outside;

        await svc.Git.RevertPathsAsync(worktree, outside, ct);
        await svc.LogAsync(runId, when, LedgerKinds.ScopeViolation, outcome: "reverted",
            payload: JsonSerializer.Serialize(new { paths = outside }), teamId: _teamId, taskId: _taskId, ct: ct);
        return outside;
    }

    private Task<bool> CommitAsync(CancellationToken ct) =>
        svc.Git.CommitAllAsync(worktree, $"{_taskId}: {task.Title}\n\nfactory-run: {runId}\nfactory-team: {_teamId}", ct);

    private ScopedWorkspace Workspace(TurnSignals signals) =>
        new(worktree, task.Writes.ToList(), handover.ProtectedPaths.ToList(), svc.Settings.CommandAllowList, signals);

    private async Task<TaskState> ApplyAsync(TaskState state, TaskEvent ev, CancellationToken ct)
    {
        var next = Core.Transition(_limits, state, ev); // throws on an illegal transition: a runner bug, not an agent error
        await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "task", LedgerKinds.TaskEvent,
            TaskId: _taskId, TeamId: _teamId, Outcome: next.GetType().Name, Payload: Core.EncodeEvent(ev)), ct);
        return next;
    }

    private ValueTask LogTurnAsync(string stage, ModelTier tier, Microsoft.Extensions.AI.UsageDetails? usage, object detail, CancellationToken ct) =>
        svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", stage, LedgerKinds.Turn,
            TaskId: _taskId, TeamId: _teamId, Model: tier.ToString(),
            TokensIn: usage?.InputTokenCount, TokensOut: usage?.OutputTokenCount,
            Payload: JsonSerializer.Serialize(detail)), ct);

    private async Task<string?> LastApproachNoteAsync(CancellationToken ct)
    {
        string? note = null;
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
        {
            if (e.Kind != LedgerKinds.Turn || e.Stage != "task_plan" || e.TaskId != _taskId || e.Payload is null) continue;
            using var doc = JsonDocument.Parse(e.Payload);
            if (doc.RootElement.TryGetProperty("note", out var n)) note = n.GetString();
        }
        return note;
    }
}
