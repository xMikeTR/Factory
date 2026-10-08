using System.Text;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.S2.Git;
using Factory.S2.Quality;
using Factory.S2.Teams;
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
        var outcomes = new List<VerifyOutcome>
        {
            await svc.Verify.RunCommandAsync(msg.RunId, "integration tests", s.IntegrationTestCommand, integration, timeout, cancellationToken),
        };

        if (s.PObserveCommand is { } pobserve)
            outcomes.Add(await svc.Verify.RunCommandAsync(msg.RunId, "PObserve", pobserve, integration, timeout, cancellationToken));

        if (Directory.Exists(s.RegistryPath))
            foreach (var d in svc.LoadHandover(msg.RunId).Decisions)
                foreach (var check in d.Checks)
                    outcomes.Add(await svc.Verify.RunCommandAsync(msg.RunId, $"{d.Id.Value} [{check.Kind}]", check.Run, s.RegistryPath, timeout, cancellationToken, d.Id.Value));
        else
            await svc.LogAsync(msg.RunId, "integration_verify", LedgerKinds.Verify, outcome: "skipped_decision_checks",
                payload: JsonSerializer.Serialize(new { reason = $"registry not found at {s.RegistryPath}" }), ct: cancellationToken);

        var failed = outcomes.Where(o => o.ExitCode != 0).ToList();
        object next = failed.Count > 0
            ? new RunFinished(msg.RunId, "integration_failed", Briefs.Failures(failed))
            : (object?)await QaAsync(msg.RunId, integration, cancellationToken) ?? new IntegrationVerified(msg.RunId);
        await context.SendMessageAsync(next, cancellationToken: cancellationToken);
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

        var branch = GitWorkspace.IntegrationBranch(msg.RunId);
        if (svc.PullRequests is GhCliPullRequests) await svc.Git.PushAsync(branch, cancellationToken);

        var url = await svc.PullRequests.EnsurePullRequestAsync(branch, s.BaseBranch,
            $"[factory] {handover.Id}: {teams.Sum(t => t.Tasks.Length)} tasks", PullRequestBody(msg.RunId, handover, teams, verdict, lines, await ReviewSectionAsync(msg.RunId, cancellationToken) + await InstallScriptsSectionAsync(msg.RunId, cancellationToken)), cancellationToken);

        // Tiered mode on a PR host: allow-listed comments record manual checks and approvals.
        var github = svc.PullRequests as IApprovalComments;
        var approvers = policy.Enforcement is MergeEnforcement.Tiered t ? t.approvers : ListModule.Empty<string>();
        var comments = github is not null && policy.Enforcement.IsTiered ? await github.CommentsAsync(url, cancellationToken) : [];
        verdict = Core.RecordVerified(approvers, comments, verdict);
        var action = Core.MergeActionFor(policy, verdict);
        var blocks = verdict.Blocks.Select(Core.DescribeBlock).ToArray();

        await svc.LogAsync(msg.RunId, "merge", LedgerKinds.Merge, outcome: action.IsBlocked ? "blocked" : verdict.Tier.ToString(),
            payload: JsonSerializer.Serialize(new { url, tier = verdict.Tier.ToString(), verdict.Reasons, blocks, lines, files = files.Count }),
            ct: cancellationToken);

        switch (action)
        {
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
            default: // MergeNow
                await svc.PullRequests.EnableAutoMergeAsync(url, cancellationToken);
                await context.SendMessageAsync(new RunFinished(msg.RunId, "auto_merge_enabled", url), cancellationToken: cancellationToken);
                break;
        }
    }

    private async ValueTask HandleApprovalAsync(MergeApprovalResponse msg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await svc.LogAsync(msg.RunId, "merge", LedgerKinds.Merge, outcome: msg.Merged ? "merged" : "not_merged",
            payload: JsonSerializer.Serialize(new { msg.ApprovedBy }), ct: cancellationToken);
        await context.SendMessageAsync(new RunFinished(msg.RunId, msg.Merged ? "merged" : "not_merged", msg.ApprovedBy ?? ""),
            cancellationToken: cancellationToken);
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

    private static string PullRequestBody(string runId, Handover handover, List<Team> teams, MergeVerdict verdict, int lines, string review)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generated by System 2 from handover `{handover.Id}` ({handover.Digest}), run `{runId}`.").AppendLine();
        if (!verdict.Blocks.IsEmpty)
        {
            sb.AppendLine("**Merge blocked** (no merge in any mode until these are cleared)");
            foreach (var b in verdict.Blocks) sb.AppendLine($"- {Core.DescribeBlock(b)}");
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
            var run = new List<LedgerEvent>();
            await foreach (var e in svc.Ledger.ReadAsync(msg.RunId, cancellationToken)) run.Add(e);
            var rows = RegistryLedgerProjector.Project(run, HandoverRef.From(svc.LoadHandover(msg.RunId)));
            await RegistryLedgerWriter.AppendAsync(svc.Settings.RegistryPath, rows, cancellationToken);
        }
        await context.YieldOutputAsync(msg, cancellationToken);
    }
}
