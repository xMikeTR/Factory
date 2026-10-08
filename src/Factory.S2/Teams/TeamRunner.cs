using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Harness.Telemetry;
using Factory.S2.Quality;
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
    private Selection? _selection; // frozen at the approach turn; contents are read fresh each turn
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

        (_approachNote, _selection) = await LastApproachAsync(ct);
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

                case TaskState.Reviewing { Item4: { } } when !answerUsed && answers.TryGetValue(_taskId, out var reviewAnswer):
                    answerUsed = true;
                    var progress = ((TaskState.Reviewing)state).Item1;
                    var answered = ReviewAnswer(reviewAnswer, progress);
                    state = await ApplyAsync(state, answered, ct);
                    if (answered is TaskEvent.ReviewAccepted accepted) // what the designer let through stays on the pull request
                        await LogReviewAsync("accepted", progress.ReviewRounds, await LastReviewTreeAsync(ct) ?? "",
                            new { note = accepted.note, accepted = progress.OpenFindings.Select(FindingJson) }, ct);
                    break;

                case TaskState.Reviewing { Item4: { } parked }:
                    return new TeamResult(_teamId, TeamStatus.Blocked,
                        new BlockedTask(_teamId, _taskId, parked.Value.Question, parked.Value.Source, parked.Value.Elements.Select(e => e.Value).ToArray(),
                            parked.Value.Options.Select(o => new OfferedOption(o.Id, o.Summary, o.Consequence)).ToArray(), parked.Value.Recommended?.Value));

                case TaskState.Reviewing rv:
                    state = await ReviewAsync(state, rv.Item1, ct);
                    break;

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
                    if (green.Count > 0 && green.All(o => o.ExitCode == 0) && await QualityOutcomeAsync(attempt, ct) is { } quality)
                        green.Add(quality); // block mode: a ratchet violation is not green
                    if (TimedOut(green) is { } greenTimeout)
                    {
                        state = await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(greenTimeout), ct);
                        break;
                    }
                    var stashed = green.Count > 0 && green.All(o => o.ExitCode == 0)
                        ? await RunWithImplStashedAsync(attempt, "stash_check", ct)
                        : [];
                    if (TimedOut(stashed) is { } stashTimeout)
                    {
                        state = await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(stashTimeout), ct);
                        break;
                    }
                    state = await ApplyAsync(state, TaskEvent.NewVerifyCompleted(Core.ToList(green), Core.ToList(stashed)), ct);
                    _lastVerifyFailed = state.IsReady; // Reviewing is a verify that passed
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

    /// One review of the verified task, or the park once the rounds are used up. A malformed review
    /// is asked for once more, then parks: a review that cannot be had is never an approval.
    private async Task<TaskState> ReviewAsync(TaskState state, Progress progress, CancellationToken ct)
    {
        if (progress.ReviewRounds >= Core.MaxReviewRounds)
            return await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(ReviewDecision(
                $"Task {_taskId} still has blocking review findings after {progress.ReviewRounds} rounds:\n{FindingLines(progress.OpenFindings)}\nWhat now?")), ct);

        var round = progress.ReviewRounds + 1;
        var diff = await svc.Git.TaskDiffAsync(worktree, runId, ct);
        var tree = await svc.Git.SnapshotAsync(worktree, ct);
        var request = new ReviewRequest(runId, _teamId, _taskId, worktree,
            Briefs.Task(handover, task, [], _approachNote, null, _resolvedDecision), diff, round, progress.OpenFindings.ToList());

        var verdict = await svc.Reviewer.ReviewAsync(request, ct);
        if (verdict is ReviewVerdict.Malformed)
            verdict = await svc.Reviewer.ReviewAsync(request, ct);
        if (verdict is ReviewVerdict.Malformed malformed)
        {
            await LogReviewAsync("malformed", round, tree, new { reason = malformed.Reason }, ct);
            return await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(ReviewDecision(
                $"Task {_taskId} could not be reviewed after two tries ({malformed.Reason}). What now?")), ct);
        }

        var seen = Diff.parse(diff).Visible;
        var kept = Review.validate(FuncConvert.FromFunc<string, int, bool>((file, line) => seen.TryGetValue(file, out var lines) && lines.Contains(line)),
            ListModule.OfSeq(((ReviewVerdict.Submitted)verdict).Findings));
        var accepted = kept.Item1;
        var dropped = kept.Item2.Select(d => (d.Item1, d.Item2)).ToList();

        // From the second round on, a new finding must sit on a line the fix changed.
        if (round > 1 && await LastReviewTreeAsync(ct) is { } previous)
        {
            var changed = Diff.parse(await svc.Git.DiffSinceAsync(worktree, previous, ct)).Added;
            var onChangedLine = FuncConvert.FromFunc<string, int, bool>((file, line) => changed.TryGetValue(file, out var lines) && lines.Contains(line));
            var late = accepted.Where(f => !Review.reReviewAllows(progress.OpenFindings, onChangedLine, f)).ToList();
            dropped.AddRange(late.Select(f => (f, $"{f.File}:{f.Line} is not on a line the fix changed")));
            accepted = ListModule.OfSeq(accepted.Where(f => !late.Contains(f)));
        }

        var split = Review.split(accepted);
        var result = new ReviewResult(split.Item1, split.Item2, svc.Reviewer.Independence);
        await LogReviewAsync(result.Blocking.IsEmpty ? "passed" : "blocked", round, tree, new
        {
            independence = result.Independence.ToString(),
            blocking = result.Blocking.Select(FindingJson),
            advisory = result.Advisory.Select(FindingJson),
            dropped = dropped.Select(d => new { finding = FindingJson(d.Item1), reason = d.Item2 }),
        }, ct);

        state = await ApplyAsync(state, TaskEvent.NewReviewCompleted(result), ct);
        if (state.IsReady)
            _lastFailures = "A reviewer who did not write this code found problems that must be fixed before the task can pass:\n" + FindingLines(result.Blocking);
        return state;
    }

    private static object FindingJson(Finding f) => new { category = f.Category.ToString(), file = f.File, line = f.Line, message = f.Message };

    private static string FindingLines(IEnumerable<Finding> findings) =>
        string.Join("\n", findings.Select(f => $"- [{f.Category}] {f.File}:{f.Line}: {f.Message}"));

    private static readonly FSharpList<DecisionOption> ReviewOptions = Core.ToList(new[]
    {
        new DecisionOption("A", "Accept the task with its open findings", "The task passes; the findings stay on the pull request for the human reviewer."),
        new DecisionOption("B", "Reject the task", "The task fails and the run does not merge."),
        new DecisionOption("C", "Send it back with guidance", "The note is given to the agent and the review rounds start again."),
    });

    private NeedsDecision ReviewDecision(string question) => new(question, task.Elements, "reviewer", ReviewOptions, null);

    /// A designer's answer to a parked review: A accepts, C guides (the note is the guidance), anything else rejects.
    private TaskEvent ReviewAnswer(DecisionAnswer answer, Progress progress)
    {
        switch (answer.DecisionId)
        {
            case "A": return TaskEvent.NewReviewAccepted(answer.Note ?? "accepted by the designer", svc.Reviewer.Independence);
            case "C":
                _lastFailures = $"A designer read the open review findings and sends the task back: {answer.Note}\n\nOpen findings:\n{FindingLines(progress.OpenFindings)}";
                return TaskEvent.NewReviewGuided(answer.Note ?? "");
            default: return TaskEvent.NewAbandon(answer.Note ?? "rejected by the designer");
        }
    }

    private ValueTask LogReviewAsync(string outcome, int round, string tree, object detail, CancellationToken ct) =>
        svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "review", LedgerKinds.Review, TaskId: _taskId, TeamId: _teamId, Outcome: outcome,
            Payload: JsonSerializer.Serialize(new { round, tree, detail })), ct);

    /// The worktree snapshot taken at this task's last review (null before the first).
    private async Task<string?> LastReviewTreeAsync(CancellationToken ct)
    {
        string? tree = null;
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
            if (e.Kind == LedgerKinds.Review && e.TaskId == _taskId && e.Payload is not null)
                tree = JsonDocument.Parse(e.Payload).RootElement.GetProperty("tree").GetString();
        return tree;
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
        var defaults = await DefaultContextAsync(ct);
        Selection Select(IReadOnlyList<Nomination> nominations) => SelectContext(defaults, nominations);
        var agent = svc.Agents.Create(new AgentScope(runId, Roles.TaskPlanner, _teamId, _taskId), tier,
            Briefs.Prompt("task-planner"), Workspace(signals, sandbox: null, []).TaskPlannerTools(Select));
        var response = await agent.RunAsync(Brief(task.Writes.Concat(task.TestWrites), null), cancellationToken: ct);

        if (signals.NeedsDecision is { } raised)
        {
            await LogTurnAsync("task_plan", tier, response.Usage, new { needs_decision = raised.Question }, ct);
            return await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(AgentDecision(raised)), ct);
        }

        // No submit_approach: the reply is the note and the pack is the defaults alone.
        _approachNote = signals.ApproachNote ?? response.Text;
        _selection = signals.ContextSelection ?? Select([]);
        await LogTurnAsync("task_plan", tier, response.Usage, new
        {
            note = _approachNote,
            context = _selection.Entries.Select(e => new { path = e.Path, reason = e.Source is ContextSource.Nominated n ? n.reason : null }),
            rejected = _selection.Rejected.Select(r => new { path = r.Item1, reason = r.Item2 }),
        }, ct);
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

        _doneDeclared = (signals.DoneSummary is not null || signals.Capped) && reverted.Count == 0;
        return await ApplyAsync(state, TaskEvent.TurnEnded, ct);
    }

    /// Feedback tools do not count against a turn's tool-call cap.
    private static readonly IReadOnlySet<string> ExemptFromCap = new HashSet<string> { "check_quality" };

    private static readonly FSharpList<DecisionOption> NoOptions = FSharpList<DecisionOption>.Empty;

    private NeedsDecision AgentDecision(RaisedDecision raised) =>
        new(raised.Question, task.Elements, "agent", Core.ToList(raised.Options), raised.Recommended is { } r ? FSharpOption<string>.Some(r) : null);

    private async Task RunActAsync(string role, string prompt, IEnumerable<PathPattern> writable, ModelTier tier, TurnSignals signals, CancellationToken ct)
    {
        await using var sandbox = await svc.Sandboxes.ForTeamAsync(_teamId, worktree, ct);
        var workspace = Workspace(signals, sandbox, writable);
        var budget = new TurnBudget(exempt: ExemptFromCap);
        var agent = svc.Agents.Create(new AgentScope(runId, role, _teamId, _taskId), tier,
            Briefs.Prompt(prompt), role == Roles.Implementer ? workspace.ImplementerTools() : workspace.ExecutorTools(), budget);
        var response = await agent.RunAsync(Brief(writable, await ContextPackAsync(ct)), cancellationToken: ct);
        // At the cap the turn counts as done: verify (or the red run) judges whatever is on the worktree.
        if (budget.Exhausted) signals.Capped = true;
        await LogTurnAsync("act", tier, response.Usage,
            new { role, files = signals.FilesWritten, done = signals.DoneSummary, capped = signals.Capped, needs_decision = signals.NeedsDecision?.Question, returned_to_test_author = signals.ReturnedToTestAuthor }, ct);
    }

    /// The TestAuthor declared done: run its tests without the implementation. They must fail;
    /// if they do, commit them (so the Implementer cannot touch them unseen) and hand over.
    private async Task<TaskState> TestsDoneAsync(TaskState state, Progress progress, CancellationToken ct)
    {
        var outcomes = await RunWithImplStashedAsync(progress.VerifyAttempts, "red_run", ct);
        if (TimedOut(outcomes) is { } timeout) return await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(timeout), ct);
        var kind = RedRun.classify(Core.ToList(outcomes));
        if (kind is RedReading.Refused refused)
        {
            // Not red evidence: the tests never ran. The TestAuthor's own project change is off the list.
            _lastFailures = "The restore was refused, so your tests did not run. Change the project files back onto the allow-list "
                + "(packages, project references, compile items, plain properties; no targets, imports, custom SDKs or package sources):\n"
                + refused.reasons;
            return state;
        }
        if (kind is RedReading.Unrunnable unrunnable)
            return await ApplyAsync(state, TaskEvent.NewNeedsDecisionRaised(new NeedsDecision(
                $"The sandbox could not run these checks, so they can never pass whatever the code does: {string.Join(", ", unrunnable.checks)}. "
                + "Fix the runner image or the check (no network during verify; only the image's tools), then resume.\n\n"
                + Briefs.Failures(outcomes.Where(o => unrunnable.checks.Contains(o.Name))),
                task.Elements, "environment", NoOptions, null)), ct);
        var red = RedEvidence.Create(Core.ToList(outcomes), ((RedReading.Red)kind).Item);
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

    /// The quality ratchet over the task's diff. Block mode returns a failing outcome that joins the
    /// green ones (so it is refused and reaches the next brief); report mode only writes the ledger row.
    private async Task<VerifyOutcome?> QualityOutcomeAsync(int attempt, CancellationToken ct)
    {
        if (svc.Settings.QualityPolicy is not { Mode: not QualityMode.Off } policy) return null;
        var started = DateTime.UtcNow;
        var violations = await QualityGate.EvaluateAsync(svc.Git, worktree, runId, policy.Thresholds, ct);
        if (violations.Count == 0) return null;
        var lines = violations.Select(v => $"- {v.Function.File}:{v.Function.Line} {v.Function.Name}: {v.Measure} {v.Value:0.#} "
            + (v.Baseline is { } b ? $"(was {b:0.#}; touched code must not get worse)" : $"(limit {v.Limit:0.#} for new code)"));
        var block = policy.Mode == QualityMode.Block;
        await svc.LogAsync(runId, "quality", LedgerKinds.Verify, block ? "blocked" : "reported",
            JsonSerializer.Serialize(new { attempt, violations = lines }), _teamId, _taskId, ct);
        return block ? new VerifyOutcome("quality", 1, (long)(DateTime.UtcNow - started).TotalMilliseconds,
            "The change leaves code harder to maintain than the policy allows. Simplify it (extract, flatten, remove branches):\n" + string.Join("\n", lines)) : null;
    }

    /// A check that timed out (exit 124 after the gate's isolated re-run) says nothing about the code, so
    /// it is neither red nor green evidence: the task parks as an environment problem instead.
    private NeedsDecision? TimedOut(IReadOnlyList<VerifyOutcome> outcomes) =>
        outcomes.Where(o => o.ExitCode == 124).Select(o => o.Name).ToList() is { Count: > 0 } names
            ? new NeedsDecision(
                $"These checks timed out, even when run alone, so they say nothing about the code: {string.Join(", ", names)}. "
                + "Raise the check's timeout, make it faster, or free the machine, then resume.\n\n"
                + Briefs.Failures(outcomes.Where(o => o.ExitCode == 124)),
                task.Elements, "environment", NoOptions, null)
            : null;

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

    private async Task<FSharpList<string>> DefaultContextAsync(CancellationToken ct) =>
        ContextPack.defaults(ListModule.OfSeq(await svc.Git.RepoFilesAsync(worktree, ct)), ListModule.OfSeq(task.Writes), ListModule.OfSeq(task.TestWrites));

    private Selection SelectContext(FSharpList<string> defaults, IReadOnlyList<Nomination> nominations) =>
        ContextPack.select(ContextPack.defaultLimits, FuncConvert.FromFunc<string, bool>(p => File.Exists(Path.Combine(worktree, p))),
            defaults, ListModule.OfSeq(nominations));

    /// What the acting turn sees up front: the frozen selection (defaults if no approach turn ran) plus
    /// everything this task has written so far, each file read now so edits since the last turn show.
    private async Task<IReadOnlyList<PackFile>> ContextPackAsync(CancellationToken ct)
    {
        _selection ??= SelectContext(await DefaultContextAsync(ct), []);
        var written = (await svc.Git.TaskPathsAsync(worktree, runId, ct)).Where(p => !p.StartsWith(".git/") && !p.StartsWith(".factory/")).ToList();
        var contents = new List<Tuple<ContextEntry, string>>();
        foreach (var entry in ContextPack.withWritten(_selection, ListModule.OfSeq(written)))
        {
            var path = Path.Combine(worktree, entry.Path);
            if (!File.Exists(path)) continue; // deleted since it was selected
            var text = await File.ReadAllTextAsync(path, ct);
            contents.Add(Tuple.Create(entry, text.Length > ContextPack.defaultLimits.MaxChars ? text[..ContextPack.defaultLimits.MaxChars] : text));
        }
        return ContextPack.fit(ContextPack.defaultLimits, ListModule.OfSeq(contents)).ToList();
    }

    private string Brief(IEnumerable<PathPattern> writable, IReadOnlyList<PackFile>? context)
    {
        var failures = _reverted is null
            ? _lastFailures
            : $"These changes were outside your writes and were reverted: {_reverted}\n\n{_lastFailures}".TrimEnd();
        return Briefs.Task(handover, task, writable, _approachNote, failures, _resolvedDecision, context);
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

    /// The approach note and the frozen context selection from the task's last task_plan turn.
    private async Task<(string? Note, Selection? Selection)> LastApproachAsync(CancellationToken ct)
    {
        string? note = null;
        Selection? selection = null;
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
        {
            if (e.Kind != LedgerKinds.Turn || e.Stage != "task_plan" || e.TaskId != _taskId || e.Payload is null) continue;
            using var doc = JsonDocument.Parse(e.Payload);
            var root = doc.RootElement;
            if (!root.TryGetProperty("note", out var n)) continue;
            note = n.GetString();
            if (!root.TryGetProperty("context", out var ctx)) { selection = null; continue; }
            var entries = ctx.EnumerateArray().Select(c => new ContextEntry(
                c.GetProperty("path").GetString()!,
                c.GetProperty("reason").ValueKind == JsonValueKind.String
                    ? ContextSource.NewNominated(c.GetProperty("reason").GetString()!)
                    : ContextSource.Default));
            var rejected = root.GetProperty("rejected").EnumerateArray()
                .Select(r => Tuple.Create(r.GetProperty("path").GetString()!, r.GetProperty("reason").GetString()!));
            selection = new Selection(ListModule.OfSeq(entries), ListModule.OfSeq(rejected));
        }
        return (note, selection);
    }
}
