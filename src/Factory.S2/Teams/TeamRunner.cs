using System.Text.Json;
using Factory.Core;
using Factory.Harness;

namespace Factory.S2.Teams;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

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

    private string? _reverted;

    /// Drive the task from whatever state the ledger says it is in to Passed (return null),
    /// Blocked or Failed (return a TeamResult). Every state change goes through ApplyAsync, which
    /// writes the ledger row first, so replay and reality cannot drift apart.
    public async Task<TeamResult?> RunAsync(IReadOnlyDictionary<string, DecisionAnswer> answers, CancellationToken ct)
    {
        var state = Core.Replay(_limits, await TaskPayloadsAsync(ct));

        // Crashed between Passed and the commit: the leftovers are this task's work, but only commit
        // them if they are all in scope (anything else is not ours to keep).
        if (state.IsPassed)
        {
            var changed = await svc.Git.ChangedPathsAsync(worktree, ct);
            if (changed.All(InScope)) await CommitAsync(ct);
            return null;
        }

        if (await svc.Git.RestoreStashAsync(worktree, StashKey, ct)) // crashed during a red run or stash check
            await svc.LogAsync(runId, "resume", LedgerKinds.Verify, outcome: "stash_restored", teamId: _teamId, taskId: _taskId, ct: ct);

        _approachNote = await LastApproachNoteAsync(ct);
        var answerUsed = false;

        while (true)
        {
            switch (state)
            {
                case TaskState.Passed:
                    await EnforceScopeAsync("task_passed", ct);
                    await CommitAsync(ct);
                    return null;

                case TaskState.Failed f:
                    return new TeamResult(_teamId, TeamStatus.Failed, Failure: $"{_taskId}: {f.Item1}");

                case TaskState.Running:
                    state = await ApplyAsync(state, TaskEvent.TurnEnded, ct); // crashed mid-turn
                    break;

                case TaskState.Blocked b when !answerUsed && answers.TryGetValue(_taskId, out var answer):
                    answerUsed = true;
                    if (answer.DecisionId is { } id)
                    {
                        _resolvedDecision = $"{id}: {answer.Note}";
                        state = await ApplyAsync(state, TaskEvent.NewDecisionBound(DecisionId.NewDecisionId(id)), ct);
                    }
                    else
                        state = await ApplyAsync(state, TaskEvent.NewAbandon(answer.Note ?? "abandoned by the designer"), ct);
                    break;

                case TaskState.Blocked b:
                    var nd = b.Item3;
                    return new TeamResult(_teamId, TeamStatus.Blocked,
                        new BlockedTask(_teamId, _taskId, nd.Question, nd.Source, nd.Elements.Select(e => e.Value).ToArray()));

                case TaskState.Verifying v:
                    var attempt = v.Item1.VerifyAttempts;
                    var green = await svc.Verify.RunTaskAsync(runId, _teamId, task, attempt, worktree, ct);
                    var stashed = green.Count > 0 && green.All(o => o.ExitCode == 0)
                        ? await RunWithImplStashedAsync(attempt, "stash_check", ct)
                        : [];
                    state = await ApplyAsync(state, TaskEvent.NewVerifyCompleted(Core.ToList(green), Core.ToList(stashed)), ct);
                    _lastVerifyFailed = !state.IsPassed;
                    if (state is TaskState.Ready { Item2.IsTestAuthor: true })
                        _lastFailures = "With the implementation stashed every test still passes, so the tests do not exercise the change: "
                            + string.Join(", ", stashed.Select(o => o.Name));
                    else if (state.IsReady) _lastFailures = Briefs.Failures(green);
                    break;

                case TaskState.Ready { Item2.IsTestAuthor: true } r:
                    // Transitional (slice C2): the tests are expected on the branch already; C3 gives
                    // the TestAuthor its own turns before this red run.
                    var red = await RedRunAsync(r.Item1, ct);
                    if (red.IsError)
                        return new TeamResult(_teamId, TeamStatus.Failed, Failure: $"{_taskId}: {red.ErrorValue}");
                    state = await ApplyAsync(state, TaskEvent.NewTestsAuthored(red.ResultValue), ct);
                    break;

                case TaskState.Ready r:
                    state = await NextTurnAsync(state, r.Item1, ct);
                    break;

                default:
                    throw new InvalidOperationException($"unknown task state {state}");
            }
        }
    }

    /// One turn from Ready: verify if the agent declared done, otherwise let the configurator
    /// route the turn (plan, act, escalate) and run it.
    private async Task<TaskState> NextTurnAsync(TaskState state, Progress progress, CancellationToken ct)
    {
        if (_doneDeclared)
        {
            _doneDeclared = false;
            await EnforceScopeAsync("pre_verify", ct);
            return await ApplyAsync(state, TaskEvent.VerifyRequested, ct);
        }

        var decision = await svc.Configurator.DecideAsync(new TurnContext(
            _taskId, progress.Turns, progress.VerifyAttempts, _approachNote is not null, _lastVerifyFailed, _turnsWithoutChange), ct);
        await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "configurator", LedgerKinds.Configurator,
            TaskId: _taskId, TeamId: _teamId, Outcome: decision.Kind.ToString(), Model: decision.Tier.ToString(),
            Payload: JsonSerializer.Serialize(new { decider = svc.Configurator.GetType().Name, reason = decision.Reason })), ct);

        return decision.Kind switch
        {
            TurnKind.Escalate => await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(new NeedsDecision(
                $"Task {_taskId} is not making progress ({decision.Reason}). How should it proceed?", task.Elements, "configurator")), ct),
            TurnKind.Plan => await PlanTurnAsync(state, decision.Tier, ct),
            TurnKind.Act => await ActTurnAsync(state, decision.Tier, ct),
            _ => throw new InvalidOperationException($"unknown turn kind {decision.Kind}"),
        };
    }

    private async Task<TaskState> PlanTurnAsync(TaskState state, ModelTier tier, CancellationToken ct)
    {
        state = await ApplyAsync(state, TaskEvent.NewTurnStarted(tier), ct);
        if (state.IsFailed) return state;

        var signals = new TurnSignals();
        var agent = svc.Agents.Create(new AgentScope(runId, Roles.TaskPlanner, _teamId, _taskId), tier,
            Briefs.Prompt("task-planner"), Workspace(signals, sandbox: null).ReadOnlyTools());
        var response = await agent.RunAsync(Brief(), cancellationToken: ct);

        if (signals.NeedsDecision is { } question)
        {
            await LogTurnAsync("task_plan", tier, response.Usage, new { needs_decision = question }, ct);
            return await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(new NeedsDecision(question, task.Elements, "agent")), ct);
        }

        _approachNote = response.Text;
        await LogTurnAsync("task_plan", tier, response.Usage, new { note = _approachNote }, ct);
        return await ApplyAsync(state, TaskEvent.TurnEnded, ct);
    }

    private async Task<TaskState> ActTurnAsync(TaskState state, ModelTier tier, CancellationToken ct)
    {
        var before = await svc.Git.WorkFingerprintAsync(worktree, ct);
        state = await ApplyAsync(state, TaskEvent.NewTurnStarted(tier), ct);
        if (state.IsFailed) return state;

        var signals = new TurnSignals();
        await using (var sandbox = await svc.Sandboxes.ForTeamAsync(_teamId, worktree, ct))
        {
            var agent = svc.Agents.Create(new AgentScope(runId, Roles.Executor, _teamId, _taskId), tier,
                Briefs.Prompt("executor"), Workspace(signals, sandbox).ExecutorTools());
            var response = await agent.RunAsync(Brief(), cancellationToken: ct);
            await LogTurnAsync("act", tier, response.Usage,
                new { files = signals.FilesWritten, done = signals.DoneSummary, needs_decision = signals.NeedsDecision }, ct);
        }

        var reverted = await EnforceScopeAsync("turn_scope", ct);
        _reverted = reverted.Count > 0 ? string.Join(", ", reverted) : null;
        _turnsWithoutChange = await svc.Git.WorkFingerprintAsync(worktree, ct) == before ? _turnsWithoutChange + 1 : 0;

        if (signals.NeedsDecision is { } question)
            return await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(new NeedsDecision(question, task.Elements, "agent")), ct);

        _doneDeclared = signals.DoneSummary is not null && reverted.Count == 0;
        return await ApplyAsync(state, TaskEvent.TurnEnded, ct);
    }

    /// The task's verify block with the implementation (changed paths under Writes) taken out.
    /// Used before implementation (red run) and after a green run (stash check).
    private async Task<List<VerifyOutcome>> RunWithImplStashedAsync(int attempt, string stage, CancellationToken ct)
    {
        var impl = (await svc.Git.ChangedPathsAsync(worktree, ct)).Where(p => task.Writes.Any(w => Core.Matches(w, p))).ToList();
        await using (await svc.Git.StashImplAsync(worktree, impl, StashKey, ct))
            return await svc.Verify.RunTaskAsync(runId, _teamId, task, attempt, worktree, ct, stage);
    }

    private async Task<Microsoft.FSharp.Core.FSharpResult<RedEvidence, string>> RedRunAsync(Progress progress, CancellationToken ct)
    {
        var outcomes = await RunWithImplStashedAsync(progress.VerifyAttempts, "red_run", ct);
        return RedEvidence.Create(Core.ToList(outcomes), Verify.VerifyRunner.ClassifyRed(outcomes));
    }

    private string StashKey => $"{runId}/{_teamId}";

    private string Brief()
    {
        var failures = _reverted is null
            ? _lastFailures
            : $"These changes were outside your writes and were reverted: {_reverted}\n\n{_lastFailures}".TrimEnd();
        return Briefs.Task(handover, task, _approachNote, failures, _resolvedDecision);
    }

    // One role writes both until the TestAuthor/Implementer split (slice C3).
    private IEnumerable<PathPattern> Writable => task.Writes.Concat(task.TestWrites);

    private bool InScope(string path) =>
        !handover.ProtectedPaths.Any(pp => Core.Matches(pp, path)) && Writable.Any(w => Core.Matches(w, path));

    private async Task<List<string>> TaskPayloadsAsync(CancellationToken ct)
    {
        var payloads = new List<string>();
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
            if (e.Kind == LedgerKinds.TaskEvent && e.TaskId == _taskId && e.Payload is not null) payloads.Add(e.Payload);
        return payloads;
    }

    /// Deterministic backstop to the workspace tools: anything changed outside the task's writes
    /// (e.g. by a build command) is reverted and logged. Returns the reverted paths.
    private async Task<List<string>> EnforceScopeAsync(string when, CancellationToken ct)
    {
        var changed = await svc.Git.ChangedPathsAsync(worktree, ct);
        var outside = changed.Where(p => !InScope(p)).ToList();
        if (outside.Count == 0) return outside;

        await svc.Git.RevertPathsAsync(worktree, outside, ct);
        await svc.LogAsync(runId, when, LedgerKinds.ScopeViolation, outcome: "reverted",
            payload: JsonSerializer.Serialize(new { paths = outside }), teamId: _teamId, taskId: _taskId, ct: ct);
        return outside;
    }

    private Task<bool> CommitAsync(CancellationToken ct) =>
        svc.Git.CommitAllAsync(worktree, $"{_taskId}: {task.Title}\n\nfactory-run: {runId}\nfactory-team: {_teamId}", ct);

    private ScopedWorkspace Workspace(TurnSignals signals, Factory.Harness.Sandbox.ICommandSandbox? sandbox) =>
        new(worktree, Writable.ToList(), handover.ProtectedPaths.ToList(), svc.Settings.CommandAllowList, signals, sandbox);

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
