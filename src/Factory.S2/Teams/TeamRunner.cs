using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Telemetry;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

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
        // Regeneration: a task no changed decision touches is carried; its work is already on base.
        var rerun = svc.LoadRerun(runId);
        var carried = team.Tasks.Where(t => rerun is not null && !rerun.Contains(t.Id.Value)).ToList();
        foreach (var task in carried)
            if (!await LoggedAsync(task.Id.Value, "carried", ct))
                await svc.LogAsync(runId, "team", LedgerKinds.Dispatch, outcome: "carried", teamId: teamId, taskId: task.Id.Value, ct: ct);
        if (carried.Count == team.Tasks.Length) return new TeamResult(teamId, TeamStatus.Passed);

        var worktree = await svc.Git.EnsureTeamAsync(runId, teamId, ct);
        IReadOnlyList<TestResult>? baseline;
        await using (await svc.Git.LeaveAsFoundAsync(worktree, ct))
            baseline = await svc.Verify.BaselineAsync(runId, teamId, svc.Settings.IntegrationTestCommand, worktree, ct);

        foreach (var task in team.Tasks.Except(carried))
        {
            var stop = await new TaskLoop(svc, handover, runId, team, task, worktree, baseline).RunAsync(answers, ct);
            if (stop is not null) return stop;
        }

        await svc.Git.MergeTeamAsync(runId, teamId, ct);
        await svc.LogAsync(runId, "team", LedgerKinds.Dispatch, outcome: "merged_to_integration", teamId: teamId, ct: ct);
        return new TeamResult(teamId, TeamStatus.Passed);
    }

    private async Task<bool> LoggedAsync(string taskId, string outcome, CancellationToken ct)
    {
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
            if (e.Kind == LedgerKinds.Dispatch && e.TaskId == taskId && e.Outcome == outcome) return true;
        return false;
    }
}

/// One task from its current state to Passed, Blocked or Failed.
/// `baseline` is the team's P2P baseline (null: the suite writes no results, so no P2P check).
internal sealed class TaskLoop(
    FactoryServices svc, Handover handover, string runId, Team team, PlannedTask task, string worktree, IReadOnlyList<TestResult>? baseline)
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
        // Every ledger row and model call below lands in this span's trace (LedgerEvent.TraceId).
        using var span = FactoryTelemetry.StartTask(runId, _teamId, task);
        var state = Core.Replay(_limits, await TaskPayloadsAsync(ct));

        // Crashed between Passed and the commit: the leftovers are this task's work, but only commit
        // them if they are all in scope (anything else is not ours to keep).
        if (state.IsPassed)
        {
            var changed = await svc.Git.ChangedPathsAsync(worktree, ct);
            if (changed.All(p => InScope(p, task.Writes))) await CommitAsync(ct); // the tests were committed at TestsAuthored
            FactoryTelemetry.RecordTaskPassed(span, true);
            return null;
        }

        if (!task.Verify.Any(v => v.IsAutomated))
        {
            FactoryTelemetry.RecordTaskPassed(span, false);
            return new TeamResult(_teamId, TeamStatus.Failed, Failure: $"{_taskId}: no automated verify entry, so there is no red run and nothing can pass");
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
                    await EnforceScopeAsync("task_passed", task.Writes, ct);
                    await CommitAsync(ct);
                    FactoryTelemetry.RecordTaskPassed(span, true);
                    return null;

                case TaskState.Failed f:
                    FactoryTelemetry.RecordTaskPassed(span, false);
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
                        new BlockedTask(_teamId, _taskId, nd.Question, nd.Source, nd.Elements.Select(e => e.Value).ToArray(),
                            nd.Options.Select(o => new OfferedOption(o.Id, o.Summary, o.Consequence)).ToArray(), nd.Recommended?.Value));

                case TaskState.Verifying v:
                    var attempt = v.Item1.VerifyAttempts;
                    FactoryTelemetry.RecordAttempt(span, attempt);
                    List<VerifyOutcome> green;
                    await using (await svc.Git.LeaveAsFoundAsync(worktree, ct))
                    {
                        green = await svc.Verify.RunTaskAsync(runId, _teamId, task, attempt, worktree, ct);
                        if (green.Count > 0 && green.All(o => o.ExitCode == 0) && baseline is not null
                            && await svc.Verify.RegressionAsync(runId, _teamId, task, baseline, svc.Settings.IntegrationTestCommand, worktree, ct) is { } regressed)
                            green.Add(regressed); // P2P: a test that passed before fails now, so this is not green
                    }
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

                case TaskState.Ready r:
                    state = await NextTurnAsync(state, r.Item1, r.Item2, ct);
                    break;

                default:
                    throw new InvalidOperationException($"unknown task state {state}");
            }
        }
    }

    /// One turn from Ready. If the agent declared done: the TestAuthor's tests get their red run,
    /// the Implementer's code gets verified. Otherwise the configurator routes the turn (plan, act,
    /// escalate) and it runs in the phase's role.
    private async Task<TaskState> NextTurnAsync(TaskState state, Progress progress, Phase phase, CancellationToken ct)
    {
        if (_doneDeclared)
        {
            _doneDeclared = false;
            if (phase.IsTestAuthor) return await TestsDoneAsync(state, progress, ct);
            await EnforceScopeAsync("pre_verify", task.Writes, ct);
            return await ApplyAsync(state, TaskEvent.VerifyRequested, ct);
        }

        var decision = await svc.Configurator.DecideAsync(new TurnContext(
            _taskId, progress.Turns, progress.VerifyAttempts, _approachNote is not null, _lastVerifyFailed, _turnsWithoutChange), ct);
        await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "configurator", LedgerKinds.Configurator,
            TaskId: _taskId, TeamId: _teamId, Outcome: decision.Kind.ToString(), Model: decision.Tier.ToString(),
            Payload: JsonSerializer.Serialize(new { decider = decision.DecidedBy, reason = decision.Reason, confidence = decision.Confidence })), ct);

        return decision.Kind switch
        {
            TurnKind.Escalate => await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(new NeedsDecision(
                $"Task {_taskId} is not making progress ({decision.Reason}). How should it proceed?", task.Elements, "configurator", NoOptions, null)), ct),
            TurnKind.Plan => await PlanTurnAsync(state, decision.Tier, ct),
            TurnKind.Act => await ActTurnAsync(state, decision.Tier, phase, ct),
            _ => throw new InvalidOperationException($"unknown turn kind {decision.Kind}"),
        };
    }

    private async Task<TaskState> PlanTurnAsync(TaskState state, ModelTier tier, CancellationToken ct)
    {
        state = await ApplyAsync(state, TaskEvent.NewTurnStarted(tier), ct);
        if (state.IsFailed) return state;

        var signals = new TurnSignals();
        var agent = svc.Agents.Create(new AgentScope(runId, Roles.TaskPlanner, _teamId, _taskId), tier,
            Briefs.Prompt("task-planner"), Workspace(signals, sandbox: null, []).ReadOnlyTools());
        var response = await agent.RunAsync(Brief(task.Writes.Concat(task.TestWrites)), cancellationToken: ct);

        if (signals.NeedsDecision is { } raised)
        {
            await LogTurnAsync("task_plan", tier, response.Usage, new { needs_decision = raised.Question }, ct);
            return await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(AgentDecision(raised)), ct);
        }

        _approachNote = response.Text;
        await LogTurnAsync("task_plan", tier, response.Usage, new { note = _approachNote }, ct);
        return await ApplyAsync(state, TaskEvent.TurnEnded, ct);
    }

    /// An acting turn in the phase's role. The TestAuthor writes only TestWrites and works with the
    /// implementation stashed (after a vacuous-tests verdict there is some), so its tests are
    /// written against the stub; the Implementer writes only Writes, the tests being committed.
    private async Task<TaskState> ActTurnAsync(TaskState state, ModelTier tier, Phase phase, CancellationToken ct)
    {
        var before = await svc.Git.WorkFingerprintAsync(worktree, ct);
        state = await ApplyAsync(state, TaskEvent.NewTurnStarted(tier), ct);
        if (state.IsFailed) return state;

        var signals = new TurnSignals();
        List<string> reverted;
        if (phase.IsTestAuthor)
        {
            await using (await svc.Git.StashImplAsync(worktree, await ImplPathsAsync(ct), StashKey, ct))
            {
                await RunActAsync(Roles.TestAuthor, "test-author", task.TestWrites, tier, signals, ct);
                reverted = await EnforceScopeAsync("turn_scope", task.TestWrites, ct); // before the implementation comes back
            }
        }
        else
        {
            await RunActAsync(Roles.Implementer, "implementer", task.Writes, tier, signals, ct);
            reverted = await EnforceScopeAsync("turn_scope", task.Writes, ct);
        }

        _reverted = reverted.Count > 0 ? string.Join(", ", reverted) : null;
        _turnsWithoutChange = await svc.Git.WorkFingerprintAsync(worktree, ct) == before ? _turnsWithoutChange + 1 : 0;

        if (signals.NeedsDecision is { } raised)
            return await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(AgentDecision(raised)), ct);

        if (signals.ReturnedToTestAuthor is { } problem && !phase.IsTestAuthor)
        {
            state = await ApplyAsync(state, TaskEvent.TurnEnded, ct);
            _lastFailures = "The implementer cannot make your tests pass from its write paths and handed them back:\n" + problem
                + "\n\nFix the tests inside your write paths. They must still fail against the current code.";
            return await ApplyAsync(state, TaskEvent.NewReturnedToTestAuthor(problem), ct);
        }

        _doneDeclared = signals.DoneSummary is not null && reverted.Count == 0;
        return await ApplyAsync(state, TaskEvent.TurnEnded, ct);
    }

    private static readonly FSharpList<DecisionOption> NoOptions = FSharpList<DecisionOption>.Empty;

    private NeedsDecision AgentDecision(RaisedDecision raised) =>
        new(raised.Question, task.Elements, "agent", Core.ToList(raised.Options), raised.Recommended is { } r ? FSharpOption<string>.Some(r) : null);

    private async Task RunActAsync(string role, string prompt, IEnumerable<PathPattern> writable, ModelTier tier, TurnSignals signals, CancellationToken ct)
    {
        await using var sandbox = await svc.Sandboxes.ForTeamAsync(_teamId, worktree, ct);
        var workspace = Workspace(signals, sandbox, writable);
        var agent = svc.Agents.Create(new AgentScope(runId, role, _teamId, _taskId), tier,
            Briefs.Prompt(prompt), role == Roles.Implementer ? workspace.ImplementerTools() : workspace.ExecutorTools());
        var response = await agent.RunAsync(Brief(writable), cancellationToken: ct);
        await LogTurnAsync("act", tier, response.Usage,
            new { role, files = signals.FilesWritten, done = signals.DoneSummary, needs_decision = signals.NeedsDecision?.Question, returned_to_test_author = signals.ReturnedToTestAuthor }, ct);
    }

    /// The TestAuthor declared done: run its tests without the implementation. They must fail;
    /// if they do, commit them (so the Implementer cannot touch them unseen) and hand over.
    private async Task<TaskState> TestsDoneAsync(TaskState state, Progress progress, CancellationToken ct)
    {
        var outcomes = await RunWithImplStashedAsync(progress.VerifyAttempts, "red_run", ct);
        var kind = RedRun.classify(Core.ToList(outcomes));
        if (kind.IsError)
            return await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(new NeedsDecision(
                $"The sandbox could not run these checks, so they can never pass whatever the code does: {string.Join(", ", kind.ErrorValue)}. "
                + "Fix the runner image or the check (no network during verify; only the image's tools), then resume.\n\n"
                + Briefs.Failures(outcomes.Where(o => kind.ErrorValue.Contains(o.Name))),
                task.Elements, "environment", NoOptions, null)), ct);
        var red = RedEvidence.Create(Core.ToList(outcomes), kind.ResultValue);
        if (red.IsError)
        {
            _lastFailures = $"Your tests pass without the implementation, so they cannot show it works ({red.ErrorValue}). "
                + "Make them fail against the current code: " + string.Join(", ", outcomes.Select(o => o.Name));
            return state;
        }

        var tests = (await svc.Git.ChangedPathsAsync(worktree, ct)).Where(p => task.TestWrites.Any(w => Core.Matches(w, p))).ToList();
        await svc.Git.CommitPathsAsync(worktree, tests, $"{_taskId} (tests): {task.Title}\n\nfactory-run: {runId}\nfactory-team: {_teamId}", ct);
        _lastFailures = null;
        return await ApplyAsync(state, TaskEvent.NewTestsAuthored(red.ResultValue), ct);
    }

    /// The task's verify block with the implementation taken out. Used for the red run (after a
    /// vacuous verdict there is an implementation to take out) and the stash check after green.
    private async Task<List<VerifyOutcome>> RunWithImplStashedAsync(int attempt, string stage, CancellationToken ct)
    {
        // Outside the stash, so the implementation it puts back counts as found.
        await using (await svc.Git.LeaveAsFoundAsync(worktree, ct))
        await using (await svc.Git.StashImplAsync(worktree, await ImplPathsAsync(ct), StashKey, ct))
            return await svc.Verify.RunTaskAsync(runId, _teamId, task, attempt, worktree, ct, stage);
    }

    /// The implementation: changed paths under Writes.
    private async Task<List<string>> ImplPathsAsync(CancellationToken ct) =>
        (await svc.Git.ChangedPathsAsync(worktree, ct)).Where(p => task.Writes.Any(w => Core.Matches(w, p))).ToList();

    private string StashKey => $"{runId}/{_teamId}";

    private string Brief(IEnumerable<PathPattern> writable)
    {
        var failures = _reverted is null
            ? _lastFailures
            : $"These changes were outside your writes and were reverted: {_reverted}\n\n{_lastFailures}".TrimEnd();
        return Briefs.Task(handover, task, writable, _approachNote, failures, _resolvedDecision);
    }

    private bool InScope(string path, IEnumerable<PathPattern> writable) =>
        !handover.ProtectedPaths.Any(pp => Core.Matches(pp, path)) && writable.Any(w => Core.Matches(w, path));

    private async Task<List<string>> TaskPayloadsAsync(CancellationToken ct)
    {
        var payloads = new List<string>();
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
            if (e.Kind == LedgerKinds.TaskEvent && e.TaskId == _taskId && e.Payload is not null) payloads.Add(e.Payload);
        return payloads;
    }

    /// Deterministic backstop to the workspace tools: anything changed outside the role's writable
    /// paths (e.g. by a build command) is reverted and logged. Returns the reverted paths.
    private async Task<List<string>> EnforceScopeAsync(string when, IEnumerable<PathPattern> writable, CancellationToken ct)
    {
        var changed = await svc.Git.ChangedPathsAsync(worktree, ct);
        var outside = changed.Where(p => !InScope(p, writable)).ToList();
        if (outside.Count == 0) return outside;

        await svc.Git.RevertPathsAsync(worktree, outside, ct);
        await svc.LogAsync(runId, when, LedgerKinds.ScopeViolation, outcome: "reverted",
            payload: JsonSerializer.Serialize(new { paths = outside }), teamId: _teamId, taskId: _taskId, ct: ct);
        return outside;
    }

    private Task<bool> CommitAsync(CancellationToken ct) =>
        svc.Git.CommitAllAsync(worktree, $"{_taskId}: {task.Title}\n\nfactory-run: {runId}\nfactory-team: {_teamId}", ct);

    private ScopedWorkspace Workspace(TurnSignals signals, Factory.Harness.Sandbox.ICommandSandbox? sandbox, IEnumerable<PathPattern> writable) =>
        new(worktree, writable.ToList(), handover.ProtectedPaths.ToList(), svc.Settings.CommandAllowList, signals, sandbox);

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
