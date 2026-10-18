using System.Text;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.S2.Git;
using Factory.S2.Quality;
using Factory.S2.Teams;
using Factory.S2.Verify;
using Microsoft.Agents.AI.Workflows;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Factory.S2.Executors;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

/// Runs every team in a wave in parallel. A blocked task gets a registry needs-decision id
/// (ND-xxxx, reused on resume). If designc has resolved every one, the wave re-runs with the
/// answers, where finished teams replay to Passed instantly; otherwise the run parks and exits,
/// and a later run with the same id picks up from the ledger.
internal sealed class WaveExecutor(FactoryServices svc) : Executor("Waves")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) => protocol
        .ConfigureRoutes(routes => routes
            .AddHandler<WaveReady>(HandleReadyAsync))
        .SendsMessage<WaveCompleted>()
        .SendsMessage<RunFinished>();

    private ValueTask HandleReadyAsync(WaveReady msg, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        new(RunWaveAsync(msg.RunId, msg.WaveIndex, msg.Answers, context, cancellationToken));

    private async Task RunWaveAsync(string runId, int index, DecisionAnswer[] answers, IWorkflowContext context, CancellationToken ct)
    {
        var handover = svc.LoadHandover(runId);
        var wave = svc.LoadDispatch(runId).Waves[index];
        var byTask = answers.ToDictionary(a => a.TaskId);

        var runner = new TeamRunner(svc, handover, runId);
        var results = await Task.WhenAll(wave.Teams.Select(team => RunTeamSafelyAsync(runner, team, byTask, ct)));

        await svc.LogAsync(runId, "wave", LedgerKinds.Dispatch, outcome: $"wave {index}",
            payload: JsonSerializer.Serialize(results.Select(r => new { r.TeamId, Status = r.Status.ToString(), r.Failure })), ct: ct);

        var failed = results.Where(r => r.Status == TeamStatus.Failed).ToList();
        var blocked = results.Where(r => r.Status == TeamStatus.Blocked).Select(r => r.Blocked!).ToArray();

        if (failed.Count > 0)
        {
            await context.SendMessageAsync(new RunFinished(runId, "failed",
                string.Join("\n", failed.Select(f => $"{f.TeamId}: {f.Failure}"))), cancellationToken: ct);
        }
        else if (blocked.Length > 0)
        {
            var ids = await NeedsDecisionIdsAsync(runId, blocked, ct);
            var resolutions = await new RegistryNeedsDecisions(svc.Settings.RegistryPath).ResolutionsAsync(ct);
            // A resolution naming an option the agent never offered answers nothing: the ND stays open.
            var answered = blocked.Select(b => (b, Answer: resolutions.TryGetValue(ids[b.TaskId], out var r)
                ? Core.AnswerDecision(ToNeedsDecision(b), r.Option!, r.Note!) : (FSharpResult<string, string>?)null)).ToList();
            var open = answered.Where(a => a.Answer is not { IsOk: true }).ToList();
            if (open.Count > 0)
            {
                foreach (var (b, answer) in open.Where(a => a.Answer is { IsError: true }))
                    await svc.LogAsync(runId, "wave", LedgerKinds.NeedsDecision, outcome: "resolution_rejected",
                        payload: JsonSerializer.Serialize(new { id = ids[b.TaskId], reason = answer!.Value.ErrorValue }), teamId: b.TeamId, taskId: b.TaskId, ct: ct);
                await context.SendMessageAsync(new RunFinished(runId, "parked",
                    string.Join(",", open.Select(a => a.Answer is { IsError: true } bad ? $"{ids[a.b.TaskId]} ({bad.ErrorValue})" : ids[a.b.TaskId]))), cancellationToken: ct);
                return;
            }
            var next = new List<DecisionAnswer>();
            foreach (var (b, answer) in answered)
            {
                var r = resolutions[ids[b.TaskId]];
                await svc.LogAsync(runId, "wave", LedgerKinds.NeedsDecision, outcome: "resolved",
                    payload: JsonSerializer.Serialize(new { id = r.Id, b.Question, b.Elements, r.Decision, r.Option, r.Note }), teamId: b.TeamId, taskId: b.TaskId, ct: ct);
                next.Add(new DecisionAnswer(b.TaskId, r.Decision ?? r.Id, answer!.Value.ResultValue));
            }
            await RunWaveAsync(runId, index, next.ToArray(), context, ct);
        }
        else
        {
            await context.SendMessageAsync(new WaveCompleted(runId, index), cancellationToken: ct);
        }
    }

    /// The ND id for each blocked task: the one already raised for the same question, or the next free one.
    private async Task<Dictionary<string, string>> NeedsDecisionIdsAsync(string runId, BlockedTask[] blocked, CancellationToken ct)
    {
        var raised = new List<(string TaskId, string Question, string Id)>();
        var resolved = new HashSet<string>();
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
        {
            if (e.Kind != LedgerKinds.NeedsDecision || e.Payload is null) continue;
            var p = JsonSerializer.Deserialize<RaisedNeedsDecision>(e.Payload, CaseInsensitive);
            if (p?.Id is null) continue;
            if (e.Outcome == "resolved") resolved.Add(p.Id);
            else raised.Add((e.TaskId ?? "", p.Question ?? "", p.Id));
        }

        var registry = new RegistryNeedsDecisions(svc.Settings.RegistryPath);
        var ids = new Dictionary<string, string>();
        foreach (var b in blocked)
        {
            var existing = raised.LastOrDefault(r => r.TaskId == b.TaskId && r.Question == b.Question && !resolved.Contains(r.Id)).Id;
            if (existing is null)
            {
                existing = await registry.NextIdAsync(raised.Select(r => r.Id), ct);
                raised.Add((b.TaskId, b.Question, existing));
                await svc.LogAsync(runId, "wave", LedgerKinds.NeedsDecision, outcome: "raised",
                    payload: JsonSerializer.Serialize(new { id = existing, b.TeamId, b.TaskId, b.Question, b.Source, b.Elements, b.Options, b.Recommended }),
                    teamId: b.TeamId, taskId: b.TaskId, ct: ct);
            }
            ids[b.TaskId] = existing;
        }
        return ids;
    }

    private sealed record RaisedNeedsDecision(string? Id, string? Question);

    private static NeedsDecision ToNeedsDecision(BlockedTask b) => new(b.Question,
        Core.ToList(b.Elements.Select(ElementId.NewElementId)), b.Source,
        Core.ToList(b.Options.Select(o => new DecisionOption(o.Id, o.Summary, o.Consequence))),
        b.Recommended is { } r ? FSharpOption<string>.Some(r) : null);
    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static async Task<TeamResult> RunTeamSafelyAsync(TeamRunner runner, Team team, IReadOnlyDictionary<string, DecisionAnswer> answers, CancellationToken ct)
    {
        try { return await runner.RunAsync(team, answers, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new TeamResult(team.Id.Value, TeamStatus.Failed, Failure: e.Message);
        }
    }
}

/// Whole-branch checks after every team has merged into integration: the full test suite, PObserve
/// over the test logs, and every bound decision's own check from the registry.
internal sealed class IntegrationVerifyExecutor(FactoryServices svc) : Executor("IntegrationVerify")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) => protocol
        .ConfigureRoutes(routes => routes
            .AddHandler<IntegrationReady>(HandleAsync))
        .SendsMessage<IntegrationVerified>()
        .SendsMessage<RunFinished>();

    private async ValueTask HandleAsync(IntegrationReady msg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var s = svc.Settings;
        var integration = svc.Git.IntegrationPath(msg.RunId);
        var timeout = TimeSpan.FromMinutes(30);

        // The frozen acceptance specs are not the coding agent's to touch: any difference from the
        // handover's hashes is a hard fail, ledgered, before anything else runs.
        var frozen = svc.LoadHandover(msg.RunId).AcceptanceSpecs;
        if (!frozen.IsEmpty)
        {
            var drift = AcceptanceSpecs.drift(frozen, ListModule.OfSeq(AcceptanceFiles.Hashes(integration).Select(h => Tuple.Create(h.Path, h.Hash))));
            if (!drift.IsEmpty)
            {
                var lines = drift.Select(AcceptanceSpecs.describe).ToList();
                await svc.LogAsync(msg.RunId, "integration_verify", LedgerKinds.Verify, outcome: "acceptance_specs_modified",
                    payload: JsonSerializer.Serialize(new { drift = lines }), ct: cancellationToken);
                await context.SendMessageAsync(new RunFinished(msg.RunId, "integration_failed",
                    "The frozen acceptance specs changed after the handover was frozen:\n" + string.Join("\n", lines.Select(l => "- " + l))),
                    cancellationToken: cancellationToken);
                return;
            }
        }

        var outcomes = new List<VerifyOutcome>
        {
            await svc.Verify.RunCommandAsync(msg.RunId, "integration tests", s.IntegrationTestCommand, integration, timeout, cancellationToken),
        };

        if (s.PObserveCommand is { } pobserve)
            outcomes.Add(await svc.Verify.RunCommandAsync(msg.RunId, "PObserve", pobserve, integration, timeout, cancellationToken));

        if (!frozen.IsEmpty)
            outcomes.Add(await AcceptanceAsync(msg.RunId, integration, cancellationToken));

        if (s.TokenLintCommand is not null && FSharpOption<string>.get_IsSome(svc.LoadHandover(msg.RunId).DesignTokens))
            outcomes.Add(await TokenLintAsync(msg.RunId, integration, cancellationToken));

        if (Directory.Exists(s.RegistryPath))
        {
            var decisions = svc.LoadHandover(msg.RunId).Decisions;
            // `p check` runs a compiled model and the registry holds sources: compile them once, in the sandbox, in a scratch copy.
            string? pModel = null;
            if (decisions.Any(d => d.Checks.Any(c => c.Kind == "p"))
                && PModel.Prepare(s.RegistryPath, Path.Combine(s.RunDir(msg.RunId), "p-model")) is { } prepared)
            {
                var compiled = await svc.Verify.RunCommandAsync(msg.RunId, "P compile", "p compile", prepared, timeout, cancellationToken);
                outcomes.Add(compiled);
                if (compiled.ExitCode == 0) pModel = prepared;
            }
            foreach (var d in decisions)
                foreach (var check in d.Checks)
                    outcomes.Add(await svc.Verify.RunCommandAsync(msg.RunId, $"{d.Id.Value} [{check.Kind}]", check.Run,
                        check.Kind == "p" && pModel is not null ? pModel : s.RegistryPath, timeout, cancellationToken, d.Id.Value));
        }
        else
            await svc.LogAsync(msg.RunId, "integration_verify", LedgerKinds.Verify, outcome: "skipped_decision_checks",
                payload: JsonSerializer.Serialize(new { reason = $"registry not found at {s.RegistryPath}" }), ct: cancellationToken);

        var failed = outcomes.Where(o => o.ExitCode != 0).ToList();
        object next = failed.Count > 0
            ? IntegrationVerdict(msg.RunId, failed)
            : (object?)await QaAsync(msg.RunId, integration, cancellationToken) ?? new IntegrationVerified(msg.RunId);
        await context.SendMessageAsync(next, cancellationToken: cancellationToken);
    }

    /// When every failure is the sandbox's, nothing ran against the code: the run is unrunnable, not failed.
    static RunFinished IntegrationVerdict(string runId, List<VerifyOutcome> failed) =>
        IntegrationRun.classify(Core.ToList(failed)) is IntegrationReading.Unrunnable u
            ? new RunFinished(runId, "integration_unrunnable",
                $"The sandbox could not run {string.Join(", ", u.checks)}, so nothing ran against the code. " +
                "Fix the runner image, or name the repo's suite in integration.testCommand in .factory/policy.yaml, then resume with --run-id.\n\n" + Briefs.Failures(failed))
            : new RunFinished(runId, "integration_failed", Briefs.Failures(failed));

    /// The handover's frozen design tokens must be well formed and keep text at or above the minimum; then the
    /// raw-value lint runs over the integrated worktree. Each violation is a TranslationError on the
    /// tasks whose `writes` cover the file, ledgered as token_lint_violation per task.
    private async Task<VerifyOutcome> TokenLintAsync(string runId, string integration, CancellationToken ct)
    {
        var tokens = DesignTokens.parse(svc.LoadHandover(runId).DesignTokens.Value);
        var problems = new List<string>();
        if (tokens.IsError) problems.AddRange(tokens.ErrorValue);
        else problems.AddRange(DesignTokens.belowMinimumText(tokens.ResultValue)
            .Select(t => $"{t.Name} is {t.Value}, below the {DesignTokens.minimumTextPx}px minimum text size"));
        if (problems.Count > 0)
        {
            await svc.LogAsync(runId, "integration_verify", LedgerKinds.Verify, outcome: "design_tokens_invalid",
                payload: JsonSerializer.Serialize(new { problems }), ct: ct);
            return new VerifyOutcome("design tokens", 1, 0, string.Join("\n", problems));
        }

        var (outcome, violations) = await svc.Verify.TokenLintAsync(runId, svc.Settings.TokenLintCommand!, integration, ct);
        if (violations.Count == 0) return outcome;

        var tasks = svc.LoadValidatedPlan(runId).Plan.Tasks;
        var (errors, orphans) = TokenLint.translationErrors(tasks, ListModule.OfSeq(violations));
        var lines = new List<string>();
        foreach (var e in errors) lines.Add($"raw values in files written by task {e.Item.Value}");
        foreach (var o in orphans) lines.Add($"{o} has raw values but no task writes it");
        foreach (var v in violations)
            foreach (var task in tasks.Where(t => t.Writes.Any(w => PathPatternModule.matches(w, v.File))))
                await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.TokenLintViolation,
                    TaskId: task.Id.Value, Outcome: "failed",
                    Payload: JsonSerializer.Serialize(new { file = v.File, line = v.Line, rule = v.Rule, text = v.Text })), ct);
        return new VerifyOutcome(outcome.Name, outcome.ExitCode, outcome.DurationMs,
            string.Join("\n", lines) + "\n\n" + string.Join("\n", violations.Select(v => $"{v.File}:{v.Line} {v.Rule}: {v.Text}")));
    }

    /// Runs the frozen specs and maps each failing one to the tasks that change its element
    /// (TranslationError of TaskId), ledgered as ui_acceptance_failed per task.
    private async Task<VerifyOutcome> AcceptanceAsync(string runId, string integration, CancellationToken ct)
    {
        var handover = svc.LoadHandover(runId);
        var (outcome, failures) = await svc.Verify.AcceptanceAsync(runId, svc.Settings.AcceptanceCommand, integration,
            handover.AcceptanceSpecs.Select(a => a.Path).ToList(), ct);
        if (failures.Count == 0) return outcome;

        var commit = await svc.Git.HeadAsync(integration, ct);
        var tasks = svc.LoadValidatedPlan(runId).Plan.Tasks;
        var lines = new List<string>();
        foreach (var f in failures)
        {
            var failure = AcceptanceSpecs.failure(handover.Elements, f.File);
            if (failure is null || !FSharpOption<SpecFailure>.get_IsSome(failure)) { lines.Add($"{f.File} failed (not an acceptance spec path)"); continue; }
            var spec = failure.Value;
            var element = FSharpOption<ElementSpec>.get_IsSome(spec.Element) ? spec.Element.Value.Id.Value : null;
            var blamed = AcceptanceSpecs.translationErrors(tasks, spec).Select(e => e.Item.Value).ToList();
            lines.Add($"{f.File} (acceptance {spec.Ac}{(element is null ? "" : $" on {element}")}) failed; " +
                      (blamed.Count == 0 ? "no task changes its element" : "tasks: " + string.Join(", ", blamed)));
            foreach (var task in blamed)
                await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "integration_verify", LedgerKinds.UiAcceptanceFailed,
                    TaskId: task, Element: element, Outcome: "failed",
                    Payload: JsonSerializer.Serialize(new { ac = spec.Ac, spec = f.File, commit, trace = f.Trace })), ct);
        }
        return new VerifyOutcome(outcome.Name, outcome.ExitCode, outcome.DurationMs, string.Join("\n", lines) + "\n\n" + outcome.OutputTail);
    }

    /// Black-box QA over the integrated result. Null means it may merge (no QA configured, or nothing found). A QA
    /// that never finished is no evidence either way, so it ends the run as an environment problem, not as a pass.
    private async Task<RunFinished?> QaAsync(string runId, string integration, CancellationToken ct)
    {
        if (svc.Qa is not { } qa) return null;
        var run = await qa.RunAsync(runId, integration, ct);
        if (run.TimedOut)
        {
            await svc.LogAsync(runId, "qa", LedgerKinds.Verify, "timeout", ct: ct);
            return new RunFinished(runId, "qa_unrunnable", "QA did not finish (environment or tests timed out), so it says nothing about the integrated result.");
        }
        var findings = Qa.judge(Core.ToList(run.Requirements), Core.ToList(run.Tests));
        await svc.LogAsync(runId, "qa", LedgerKinds.Verify, findings.IsEmpty ? "passed" : "failed",
            JsonSerializer.Serialize(new { requirements = run.Requirements, tests = run.Tests.Count, findings = findings.Select(f => f.ToString()) }), ct: ct);
        return findings.IsEmpty ? null : new RunFinished(runId, "qa_failed", QaGate.Brief(findings));
    }
}

/// Opens the PR and applies the merge policy (decided in F#). Any block ends the run unmerged in
/// every mode; otherwise merge-now enables GitHub auto-merge and higher tiers wait on the
/// merge-approval port.
internal sealed class MergeExecutor(FactoryServices svc) : Executor("Merge")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) => protocol
        .ConfigureRoutes(routes => routes
            .AddHandler<IntegrationVerified>(HandleVerifiedAsync)
            .AddHandler<MergeApprovalResponse>(HandleApprovalAsync))
        .SendsMessage<MergeApprovalRequest>()
        .SendsMessage<RunFinished>();

    private async ValueTask HandleVerifiedAsync(IntegrationVerified msg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var s = svc.Settings;
        var handover = svc.LoadHandover(msg.RunId);
        var dispatch = svc.LoadDispatch(msg.RunId);
        var teams = dispatch.Waves.SelectMany(w => w.Teams).ToList();
        var (lines, files) = await svc.Git.DiffStatsAsync(msg.RunId, s.BaseBranch, cancellationToken);

        var policy = s.MergePolicy ?? Core.DefaultMergePolicy;
        var verdict = Core.DecideMerge(policy, new MergeInput(Core.ToList(teams), handover, lines, Core.ToList(files)));
        // Manual checks a person recorded with `factory-s2 verify` (done or deferred) clear their blocks in any mode.
        var manual = Core.ToList(await ManualChecks.ReadAsync(svc.Ledger, msg.RunId, cancellationToken));
        verdict = Merge.recordManual(policy.Enforcement, manual, verdict);
        var deferred = ManualChecks.Deferred(policy.Enforcement, manual);

        var branch = GitWorkspace.IntegrationBranch(msg.RunId);
        if (svc.PullRequests is GhCliPullRequests) await svc.Git.PushAsync(branch, cancellationToken);

        var url = await svc.PullRequests.EnsurePullRequestAsync(branch, s.BaseBranch,
            $"[factory] {handover.Id}: {teams.Sum(t => t.Tasks.Length)} tasks", PullRequestBody(msg.RunId, handover, teams, verdict, deferred, lines, await ReviewSectionAsync(msg.RunId, cancellationToken) + await InstallScriptsSectionAsync(msg.RunId, cancellationToken) + await QualitySectionAsync(msg.RunId, cancellationToken)), cancellationToken);

        // Tiered mode on a PR host: allow-listed comments record manual checks and approvals.
        var github = svc.PullRequests as IApprovalComments;
        var approvers = policy.Enforcement is MergeEnforcement.Tiered t ? t.approvers : ListModule.Empty<string>();
        var comments = github is not null && policy.Enforcement.IsTiered ? await github.CommentsAsync(url, cancellationToken) : [];
        verdict = Core.RecordVerified(approvers, comments, verdict);
        var action = Core.MergeActionFor(policy, verdict);
        var blocks = verdict.Blocks.Select(Core.DescribeBlock).ToArray();

        await svc.LogAsync(msg.RunId, "merge", LedgerKinds.Merge, outcome: action.IsBlocked ? "blocked" : verdict.Tier.ToString(),
            payload: JsonSerializer.Serialize(new { url, tier = verdict.Tier.ToString(), verdict.Reasons, blocks, deferred, lines, files = files.Count }),
            ct: cancellationToken);

        switch (action)
        {
            case MergeAction.Blocked when verdict.Blocks.All(b => b.IsManualVerifyPending):
                // Nothing failed: the run waits on a person doing a check, so it parks and says how to record it.
                await context.SendMessageAsync(new RunFinished(msg.RunId, "parked", ManualWait(msg.RunId, teams, verdict.Blocks)), cancellationToken: cancellationToken);
                break;
            case MergeAction.Blocked:
                await context.SendMessageAsync(new RunFinished(msg.RunId, "merge_blocked", string.Join("\n", blocks)), cancellationToken: cancellationToken);
                break;
            case MergeAction.AwaitApproval approval when github is not null:
                var a = Core.Approvals(approval.tier, approval.approvers, comments);
                if (!a.Approved)
                {
                    await context.SendMessageAsync(new RunFinished(msg.RunId, "parked",
                        $"awaiting_approval {url} ({a.Missing} more /approve from {string.Join(", ", approval.approvers)})"), cancellationToken: cancellationToken);
                    break;
                }
                await github.MergeAsync(url, cancellationToken);
                await svc.LogAsync(msg.RunId, "merge", LedgerKinds.Merge, outcome: "merged",
                    payload: JsonSerializer.Serialize(new { url, by = a.By, single_operator = a.SingleOperator }), ct: cancellationToken);
                await context.SendMessageAsync(new RunFinished(msg.RunId, "merged", string.Join(", ", a.By)), cancellationToken: cancellationToken);
                break;
            case MergeAction.AwaitApproval approval: // no PR host to read: ask a human
                await context.SendMessageAsync(new MergeApprovalRequest(msg.RunId, url, approval.tier.ToString(), verdict.Reasons.ToArray()),
                    cancellationToken: cancellationToken);
                break;
            case { IsMergeNow: true } when github is null:
                // No PR host can merge: a person merges the branch and says so, so the run never claims a merge that did not happen.
                await context.SendMessageAsync(new MergeApprovalRequest(msg.RunId, url, verdict.Tier.ToString(),
                    [$"no pull request host: merge {branch} into {s.BaseBranch} yourself", .. verdict.Reasons]), cancellationToken: cancellationToken);
                break;
            default: // MergeNow
                await svc.PullRequests.EnableAutoMergeAsync(url, cancellationToken);
                await context.SendMessageAsync(new RunFinished(msg.RunId, "auto_merge_enabled", url), cancellationToken: cancellationToken);
                break;
        }
    }

    /// "waiting on a manual check: T-001: name (instructions)" per pending check, then the command that records one.
    private string ManualWait(string runId, IEnumerable<Team> teams, IEnumerable<MergeBlock> pending)
    {
        var instructions = teams.SelectMany(t => t.Tasks).SelectMany(t => t.Verify.OfType<VerifyEntry.Manual>()
            .Select(m => ($"{t.Id.Value}: {m.Item.Name}", m.Item.Instructions))).ToDictionary(x => x.Item1, x => x.Instructions);
        var checks = pending.OfType<MergeBlock.ManualVerifyPending>().Select(b => b.check).ToList();
        var lines = checks.Select(c => $"waiting on a manual check: {c} ({instructions.GetValueOrDefault(c, "no instructions")})");
        return string.Join("\n", lines) +
            $"\nRecord it with: factory-s2 verify --repo {svc.Settings.RepoPath} --run-id {runId} --check \"{checks[0]}\" --by <you> [--outcome deferred --note \"why\"], then run again with --run-id {runId}";
    }

    private async ValueTask HandleApprovalAsync(MergeApprovalResponse msg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await svc.LogAsync(msg.RunId, "merge", LedgerKinds.Merge, outcome: msg.Merged ? "merged" : "not_merged",
            payload: JsonSerializer.Serialize(new { msg.ApprovedBy }), ct: cancellationToken);
        await context.SendMessageAsync(new RunFinished(msg.RunId, msg.Merged ? "merged" : "not_merged", msg.ApprovedBy ?? ""),
            cancellationToken: cancellationToken);
    }

    private async Task<string> QualitySectionAsync(string runId, CancellationToken ct)
    {
        var rows = new List<QualityRow>();
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
            if (e is { Kind: LedgerKinds.Verify, Stage: "quality" or "mutation", TaskId: not null, Outcome: not null, Payload: not null })
                rows.Add(new QualityRow(e.Stage, e.TaskId, e.Outcome, e.Payload));
        return QualityReport.Section(rows) is { } section ? "\n" + section : "";
    }

    private async Task<string> InstallScriptsSectionAsync(string runId, CancellationToken ct)
    {
        var rows = new List<string>();
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
            if (e.Stage == "restore" && e.Payload is not null) rows.Add(e.Payload);
        return InstallScriptReport.Section(rows) is { } section ? "\n" + section : "";
    }

    /// Who reviewed (and whether they were independent of the implementer), the advice that never
    /// blocked, and any findings a designer let through at the round limit.
    private async Task<string> ReviewSectionAsync(string runId, CancellationToken ct)
    {
        var advice = new List<string>();
        var accepted = new List<string>();
        await foreach (var e in svc.Ledger.ReadAsync(runId, ct))
        {
            if (e.Kind != LedgerKinds.Review || e.Payload is null) continue;
            var detail = JsonDocument.Parse(e.Payload).RootElement.GetProperty("detail");
            if (e.Outcome == "passed")
                advice.AddRange(detail.GetProperty("advisory").EnumerateArray().Select(f => Describe(e.TaskId, f)));
            else if (e.Outcome == "accepted")
                accepted.AddRange(detail.GetProperty("accepted").EnumerateArray().Select(f => Describe(e.TaskId, f)));
        }

        var sb = new StringBuilder("### Review\n");
        sb.AppendLine(svc.Reviewer.Independence.IsSameFamily
            ? "**Reviewed by the same model family as the implementer** (`independence: same-family`): not an independent check."
            : "Reviewed by a model family independent of the implementer (`independence: independent`).");
        if (advice.Count > 0) { sb.AppendLine().AppendLine("Advice (never blocked):"); foreach (var a in advice) sb.AppendLine($"- {a}"); }
        if (accepted.Count > 0) { sb.AppendLine().AppendLine("Accepted by a designer with these findings open:"); foreach (var a in accepted) sb.AppendLine($"- {a}"); }
        return sb.ToString();

        static string Describe(string? task, JsonElement f) =>
            $"{task} [{f.GetProperty("category").GetString()}] {f.GetProperty("file").GetString()}:{f.GetProperty("line").GetInt32()}: {f.GetProperty("message").GetString()}";
    }

    private static string PullRequestBody(string runId, Handover handover, List<Team> teams, MergeVerdict verdict, IReadOnlyList<string> deferred, int lines, string review)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generated by System 2 from handover `{handover.Id}` ({handover.Digest}), run `{runId}`.").AppendLine();
        if (!verdict.Blocks.IsEmpty)
        {
            sb.AppendLine("**Merge blocked** (no merge in any mode until these are cleared)");
            foreach (var b in verdict.Blocks) sb.AppendLine($"- {Core.DescribeBlock(b)}");
            sb.AppendLine();
        }
        if (deferred.Count > 0)
        {
            sb.AppendLine("**Manual checks deferred** (not done; a person accepted merging without them)");
            foreach (var d in deferred) sb.AppendLine($"- {d}");
            sb.AppendLine();
        }
        sb.AppendLine($"**Merge tier: {verdict.Tier}**");
        foreach (var r in verdict.Reasons) sb.AppendLine($"- {r}");
        sb.AppendLine().AppendLine("### Decisions");
        foreach (var d in handover.Decisions) sb.AppendLine($"- {d.Id.Value}: {d.Statement}");
        sb.AppendLine().AppendLine("### Tasks");
        foreach (var t in teams.SelectMany(t => t.Tasks))
        {
            sb.AppendLine($"- {t.Id.Value}: {t.Title}");
            foreach (var v in t.Verify)
                if (v is VerifyEntry.Manual { Item: var m }) sb.AppendLine($"  - [ ] manual: {m.Name}. {m.Instructions}");
        }
        sb.AppendLine().Append(review);
        sb.AppendLine().AppendLine($"{lines} lines changed. Every task passed its verify block; integration tests, PObserve and decision checks passed.");
        return sb.ToString();
    }
}

internal sealed class FinalizeExecutor(FactoryServices svc) : Executor("Finalize")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) => protocol
        .ConfigureRoutes(routes => routes
            .AddHandler<RunFinished>(HandleAsync))
        .YieldsOutput<RunFinished>();

    private async ValueTask HandleAsync(RunFinished msg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await svc.LogAsync(msg.RunId, "finalize", LedgerKinds.Run, outcome: msg.Status, payload: JsonSerializer.Serialize(msg), ct: cancellationToken);
        if (Directory.Exists(svc.Settings.RegistryPath))
        {
            await RegistryProgress.SyncAsync(svc.Ledger, svc.Settings.RegistryPath, msg.RunId, HandoverRef.From(svc.LoadHandover(msg.RunId)), cancellationToken);
        }
        await context.YieldOutputAsync(msg, cancellationToken);
    }
}
