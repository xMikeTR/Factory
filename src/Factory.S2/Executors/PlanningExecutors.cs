using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Harness;
using Factory.S2.Teams;
using Microsoft.Agents.AI.Workflows;

namespace Factory.S2.Executors;

using Core = Factory.Core.Core; // "Core" alone binds to the namespace Factory.Core from inside Factory.*

// Executors declare routes and sent types in ConfigureProtocol by hand. The [MessageHandler] source
// generator (Microsoft.Agents.AI.Workflows.Generators) needs the .NET 10 Roslyn host, and explicit
// routes keep each executor's message contract readable in one place.

/// Turns the handover package into plan.json with the planner agent. On rejection it gets the
/// validator's errors verbatim and tries again, up to MaxPlannerAttempts.
internal sealed class PlannerExecutor(FactoryServices svc) : Executor("Planner")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) => protocol
        .ConfigureRoutes(routes => routes
            .AddHandler<StartRun>(HandleStartAsync)
            .AddHandler<PlanRejected>(HandleRejectedAsync))
        .SendsMessage<PlanDraft>()
        .SendsMessage<RunFinished>();

    private async ValueTask HandleStartAsync(StartRun msg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var runDir = svc.Settings.RunDir(msg.RunId);
        Directory.CreateDirectory(runDir);
        var handoverCopy = svc.Settings.HandoverCopyPath(msg.RunId);
        if (!File.Exists(handoverCopy)) File.Copy(msg.HandoverPath, handoverCopy);
        var handover = svc.LoadHandover(msg.RunId); // fail fast on a malformed package

        // Regeneration: start from the previous run's plan, stamped with this freeze. The validator
        // still decides; if the plan no longer fits the handover, the planner redrafts as usual.
        if (msg.Since is { } since && !File.Exists(svc.Settings.PlanPath(msg.RunId)))
        {
            var plan = JsonNode.Parse(await File.ReadAllTextAsync(svc.Settings.PlanPath(since), cancellationToken))!;
            plan["handover"] = new JsonObject { ["id"] = handover.Id, ["digest"] = handover.Digest };
            await File.WriteAllTextAsync(svc.Settings.SincePath(msg.RunId), since, cancellationToken);
            await File.WriteAllTextAsync(svc.Settings.PlanPath(msg.RunId), plan.ToJsonString(), cancellationToken);
        }

        // Resume: a stored plan that still validates is reused, not regenerated.
        if (File.Exists(svc.Settings.PlanPath(msg.RunId)))
        {
            await context.SendMessageAsync(new PlanDraft(msg.RunId, 0), cancellationToken: cancellationToken);
            return;
        }

        await DraftAsync(msg.RunId, 1, previous: null, errors: null, context, cancellationToken);
    }

    private async ValueTask HandleRejectedAsync(PlanRejected msg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        if (msg.Attempt >= svc.Settings.MaxPlannerAttempts)
        {
            await context.SendMessageAsync(new RunFinished(msg.RunId, "plan_rejected",
                $"planner did not produce a valid plan in {msg.Attempt} attempts:\n- " + string.Join("\n- ", msg.Errors)),
                cancellationToken: cancellationToken);
            return;
        }
        var previous = File.Exists(svc.Settings.PlanPath(msg.RunId)) ? await File.ReadAllTextAsync(svc.Settings.PlanPath(msg.RunId), cancellationToken) : null;
        await DraftAsync(msg.RunId, msg.Attempt + 1, previous, msg.Errors, context, cancellationToken);
    }

    private async Task DraftAsync(string runId, int attempt, string? previous, string[]? errors, IWorkflowContext context, CancellationToken ct)
    {
        var handoverJson = await File.ReadAllTextAsync(svc.Settings.HandoverCopyPath(runId), ct);
        using var schema = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "prompts", "plan.schema.json"), ct));
        var handover = svc.LoadHandover(runId);

        var brief = $"""
            ## Handover package
            ```json
            {handoverJson}
            ```
            """;
        if (errors is not null)
            brief += $"""


                ## Your previous turn ended without an accepted plan. Fix every one of these and submit the whole plan again.
                - {string.Join("\n- ", errors)}
                """ + (previous is null ? "" : $"""


                ```json
                {previous}
                ```
                """);

        var signals = new TurnSignals();
        var ws = new ScopedWorkspace(svc.Settings.RepoPath, [], [], [], signals);
        var agent = svc.Agents.Create(new AgentScope(runId, Roles.Planner), ModelTier.Frontier, Briefs.Prompt("planner"),
            ws.PlannerTools(schema.RootElement, plan => Core.ValidatePlan(handover, plan).Errors));
        var response = await agent.RunAsync(brief, cancellationToken: ct);

        if (signals.SubmittedPlan is { } plan) await File.WriteAllTextAsync(svc.Settings.PlanPath(runId), plan, ct);
        else File.Delete(svc.Settings.PlanPath(runId));
        await svc.Ledger.AppendAsync(new LedgerEvent(runId, "S2", "plan", LedgerKinds.Plan, Outcome: "drafted",
            Model: "frontier", TokensIn: response.Usage?.InputTokenCount, TokensOut: response.Usage?.OutputTokenCount,
            Payload: JsonSerializer.Serialize(new { attempt })), ct);

        await context.SendMessageAsync(new PlanDraft(runId, attempt), cancellationToken: ct);
    }

}

/// Deterministic gate on the planner's output. The planner proposes; this decides.
internal sealed class PlanValidatorExecutor(FactoryServices svc) : Executor("PlanValidator")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) => protocol
        .ConfigureRoutes(routes => routes
            .AddHandler<PlanDraft>(HandleAsync))
        .SendsMessage<PlanAccepted>()
        .SendsMessage<PlanRejected>();

    private async ValueTask HandleAsync(PlanDraft msg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var path = svc.Settings.PlanPath(msg.RunId);
        var validation = File.Exists(path)
            ? Core.ValidatePlan(svc.LoadHandover(msg.RunId), await File.ReadAllTextAsync(path, cancellationToken))
            : new PlanValidation(null!, ["No plan was submitted: call submit_plan with the whole plan."]);

        await svc.LogAsync(msg.RunId, "plan_validate", LedgerKinds.Plan,
            outcome: validation.IsValid ? "accepted" : "rejected",
            payload: JsonSerializer.Serialize(new { msg.Attempt, validation.Errors }), ct: cancellationToken);

        object next = validation.IsValid
            ? new PlanAccepted(msg.RunId)
            : new PlanRejected(msg.RunId, Math.Max(msg.Attempt, 1), validation.Errors);
        await context.SendMessageAsync(next, cancellationToken: cancellationToken);
    }
}

/// Teams and waves (pure F#), the integration branch, then one wave at a time.
internal sealed class DispatchExecutor(FactoryServices svc) : Executor("Dispatcher")
{
    protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocol) => protocol
        .ConfigureRoutes(routes => routes
            .AddHandler<PlanAccepted>(HandleAcceptedAsync)
            .AddHandler<WaveCompleted>(HandleWaveCompletedAsync))
        .SendsMessage<WaveReady>()
        .SendsMessage<IntegrationReady>();

    private async ValueTask HandleAcceptedAsync(PlanAccepted msg, IWorkflowContext context, CancellationToken cancellationToken = default)
    {
        var dispatch = svc.LoadDispatch(msg.RunId);
        var json = Core.DispatchJson(dispatch);
        await File.WriteAllTextAsync(svc.Settings.DispatchPath(msg.RunId), json, cancellationToken);
        await svc.LogAsync(msg.RunId, "dispatch", LedgerKinds.Dispatch, outcome: $"{dispatch.Waves.Length} waves", payload: json, ct: cancellationToken);
        await RegenerationAsync(msg.RunId, cancellationToken);

        await svc.Git.EnsureIntegrationAsync(msg.RunId, svc.Settings.BaseBranch, cancellationToken);
        await NextAsync(msg.RunId, 0, dispatch, context, cancellationToken);
    }

    /// A run --since an earlier freeze: decide once which tasks re-run, from the decisions that changed.
    private async Task RegenerationAsync(string runId, CancellationToken ct)
    {
        var s = svc.Settings;
        if (!File.Exists(s.SincePath(runId)) || File.Exists(s.RegenerationPath(runId))) return;
        var since = (await File.ReadAllTextAsync(s.SincePath(runId), ct)).Trim();
        var plan = svc.LoadValidatedPlan(runId);
        var r = Core.Regenerate(svc.LoadHandover(since), plan);
        var json = JsonSerializer.Serialize(new RegenerationFile(since,
            r.Changed.Select(d => d.Value).ToArray(), r.Rerun.Select(t => t.Value).ToArray()));
        await File.WriteAllTextAsync(s.RegenerationPath(runId), json, ct);
        await svc.LogAsync(runId, "regenerate", LedgerKinds.Plan,
            outcome: $"{r.Rerun.Length} of {plan.Plan.Tasks.Length} tasks re-run", payload: json, ct: ct);
    }

    private ValueTask HandleWaveCompletedAsync(WaveCompleted msg, IWorkflowContext context, CancellationToken cancellationToken = default) =>
        new(NextAsync(msg.RunId, msg.WaveIndex + 1, svc.LoadDispatch(msg.RunId), context, cancellationToken));

    private static async Task NextAsync(string runId, int index, DispatchPlan dispatch, IWorkflowContext context, CancellationToken ct)
    {
        object next = index < dispatch.Waves.Length
            ? new WaveReady(runId, index, [])
            : new IntegrationReady(runId);
        await context.SendMessageAsync(next, cancellationToken: ct);
    }
}
