using System.Text;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.S2.Git;
using Factory.S2.Teams;
using Microsoft.Agents.AI.Workflows;

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
            var open = blocked.Where(b => !resolutions.ContainsKey(ids[b.TaskId])).ToList();
            if (open.Count > 0)
            {
                await context.SendMessageAsync(new RunFinished(runId, "parked",
                    string.Join(",", open.Select(b => ids[b.TaskId]))), cancellationToken: ct);
                return;
            }
            var next = new List<DecisionAnswer>();
            foreach (var b in blocked)
            {
                var r = resolutions[ids[b.TaskId]];
                await svc.LogAsync(runId, "wave", LedgerKinds.NeedsDecision, outcome: "resolved",
                    payload: JsonSerializer.Serialize(new { id = r.Id, b.Question, b.Elements, r.Decision, r.Note }), teamId: b.TeamId, taskId: b.TaskId, ct: ct);
                next.Add(new DecisionAnswer(b.TaskId, r.Decision ?? r.Id,
                    r.Note ?? (r.Decision is null ? "resolved in the registry; re-read the handover and decisions" : null)));
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
                    payload: JsonSerializer.Serialize(new { id = existing, b.TeamId, b.TaskId, b.Question, b.Source, b.Elements }),
                    teamId: b.TeamId, taskId: b.TaskId, ct: ct);
            }
            ids[b.TaskId] = existing;
        }
        return ids;
    }

    private sealed record RaisedNeedsDecision(string? Id, string? Question);
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
        object next = failed.Count == 0
            ? new IntegrationVerified(msg.RunId)
            : new RunFinished(msg.RunId, "integration_failed", Briefs.Failures(failed));
        await context.SendMessageAsync(next, cancellationToken: cancellationToken);
    }
}

/// Opens the PR and applies tiered merge autonomy (decided in F#). Auto-merge tier enables
/// GitHub auto-merge; other tiers wait on the merge-approval port.
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

        var verdict = Core.DecideMerge(Core.DefaultMergePolicy,
            new MergeInput(Core.ToList(teams), handover, lines, Core.ToList(files)));

        var branch = GitWorkspace.IntegrationBranch(msg.RunId);
        if (svc.PullRequests is GhCliPullRequests) await svc.Git.PushAsync(branch, cancellationToken);

        var url = await svc.PullRequests.EnsurePullRequestAsync(branch, s.BaseBranch,
            $"[factory] {handover.Id}: {teams.Sum(t => t.Tasks.Length)} tasks", PullRequestBody(msg.RunId, handover, teams, verdict, lines), cancellationToken);

        await svc.LogAsync(msg.RunId, "merge", LedgerKinds.Merge, outcome: verdict.Tier.ToString(),
            payload: JsonSerializer.Serialize(new { url, verdict.Reasons, lines, files = files.Count }), ct: cancellationToken);

        if (verdict.Tier.IsAutoMerge)
        {
            await svc.PullRequests.EnableAutoMergeAsync(url, cancellationToken);
            await context.SendMessageAsync(new RunFinished(msg.RunId, "auto_merge_enabled", url), cancellationToken: cancellationToken);
        }
        else
        {
            await context.SendMessageAsync(new MergeApprovalRequest(msg.RunId, url, verdict.Tier.ToString(), verdict.Reasons.ToArray()),
                cancellationToken: cancellationToken);
        }
    }

    private async ValueTask HandleApprovalAsync(MergeApprovalResponse msg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        await svc.LogAsync(msg.RunId, "merge", LedgerKinds.Merge, outcome: msg.Merged ? "merged" : "not_merged",
            payload: JsonSerializer.Serialize(new { msg.ApprovedBy }), ct: cancellationToken);
        await context.SendMessageAsync(new RunFinished(msg.RunId, msg.Merged ? "merged" : "not_merged", msg.ApprovedBy ?? ""),
            cancellationToken: cancellationToken);
    }

    private static string PullRequestBody(string runId, Handover handover, List<Team> teams, MergeVerdict verdict, int lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Generated by System 2 from handover `{handover.Id}` ({handover.Digest}), run `{runId}`.").AppendLine();
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
