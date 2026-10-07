using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Workflow;

using Core = Factory.Core.Core;

public class S2WorkflowTests
{
    private const string NewFile = "src/Pricing.Data/QuoteStatus.cs";

    private sealed class ApprovingHuman : IHumanChannel
    {
        public List<MergeApprovalRequest> Approvals { get; } = [];
        public Task<NeedsDecisionResponse> AskAsync(NeedsDecisionRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("no decision expected");
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct)
        {
            Approvals.Add(request);
            return Task.FromResult(new MergeApprovalResponse(request.RunId, true, "test-approver"));
        }
    }

    private static string PlanFor(Handover h) => JsonSerializer.Serialize(new
    {
        handover = new { id = h.Id, digest = h.Digest },
        tasks = new[]
        {
            new
            {
                id = "T-001", title = "Add QuoteStatus", instructions = "Add the QuoteStatus enum.",
                elements = new[] { "Pricing Platform/Quotes DB" },
                writes = new[] { "src/Pricing.Data/**" },
                testWrites = new[] { "tests/Pricing.Data.Tests/**" },
                dependsOn = Array.Empty<string>(),
                covers = new[] { "D-0001" },
                verify = new[] { new { name = "file exists", kind = "invariant", run = $"sh {TeamHarness.QuoteStatusTest}", timeoutSeconds = 30 } },
            },
        },
    });

    [Fact]
    public async Task Dry_run_on_T001_replays_to_Passed_and_writes_the_PR_body()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        model.Enqueue(
            Say(PlanFor(h.Handover)),                                   // planner
            Say("Approach: add QuoteStatus.cs"),                        // task-planner
            Call("declare_done", new { summary = "test already on main" }), Say("done"), // test author
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));
        var human = new ApprovingHuman();

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services),
            new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), human, default);

        Assert.NotNull(result);
        Assert.Equal("merged", result!.Status);

        var payloads = h.Rows(LedgerKinds.TaskEvent).Where(e => e.TaskId == "T-001").Select(e => e.Payload!);
        Assert.True(Core.Replay(new Limits(8, 3), payloads).IsPassed);

        var approval = Assert.Single(human.Approvals);
        Assert.Equal("TwoApprovals", approval.Tier); // Quotes DB is high risk

        var prBody = Path.Combine(h.StateDir, "runs", TeamHarness.RunId, $"factory_{TeamHarness.RunId}_integration.pr.md");
        Assert.True(File.Exists(prBody));
        Assert.Contains("Merge tier: TwoApprovals", await File.ReadAllTextAsync(prBody));
        Assert.Single(h.Rows(LedgerKinds.Run), e => e.Outcome == "merged");
    }

    [Fact]
    public async Task A_finished_run_projects_its_tasks_and_decision_checks_into_the_registry_ledger()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        Directory.CreateDirectory(h.Services.Settings.RegistryPath); // empty registry: D-0001's P check fails there
        model.Enqueue(
            Say(PlanFor(h.Handover)),
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services),
            new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default);

        Assert.Equal("integration_failed", result?.Status);
        var ledger = Path.Combine(h.Services.Settings.RegistryPath, "ledger");
        var pipeline = await File.ReadAllLinesAsync(Path.Combine(ledger, "pipeline.jsonl"));
        var checks = await File.ReadAllLinesAsync(Path.Combine(ledger, "checks.jsonl"));
        Assert.All(pipeline.Concat(checks), l => Assert.Empty(DesigncSchema.Validate(l)));
        Assert.Single(pipeline, l => l.Contains("\"task\":\"T-001\"") && l.Contains("\"status\":\"passed\""));
        var check = JsonDocument.Parse(Assert.Single(checks)).RootElement;
        Assert.Equal("D-0001", check.GetProperty("decision").GetString());
        Assert.Equal("p check -tc tcOptimistic", check.GetProperty("check").GetString());
        Assert.Equal("fail", check.GetProperty("outcome").GetString());
        Assert.NotEqual(0, check.GetProperty("exit_code").GetInt32());
    }

    [Fact]
    public async Task Resume_after_a_kill_mid_turn_reaches_the_same_state_without_duplicates()
    {
        using var killed = new CancellationTokenSource();
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        model.Enqueue(
            Say(PlanFor(h.Handover)),
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Kill(killed));
        var handover = h.WriteHandoverJson();

        try { await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, handover), new ApprovingHuman(), killed.Token); }
        catch (OperationCanceledException) { }
        Assert.Equal(0, model.Remaining);
        Assert.Empty(h.Rows(LedgerKinds.Run));
        Assert.Equal("Running", h.Rows(LedgerKinds.TaskEvent).Last(e => e.TaskId == "T-001").Outcome); // died mid-turn

        // restart: same run id, fresh workflow; the model only sees what is left to do
        model.Enqueue(Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));
        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, handover), new ApprovingHuman(), default);

        Assert.Equal("merged", result?.Status);
        Assert.Equal(0, model.Remaining);
        Assert.Single(h.Rows(LedgerKinds.Plan), e => e.Outcome == "drafted");       // plan reused, not redrafted
        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.TaskId == "T-001" && e.Stage == "verify");          // verified once
        var events = h.Rows(LedgerKinds.TaskEvent).Where(e => e.TaskId == "T-001").ToList();
        Assert.True(Core.Replay(new Limits(8, 3), events.Select(e => e.Payload!)).IsPassed);
        Assert.Single(events, e => e.Outcome == "Passed");

        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        var log = await h.GitIn(integration, "log", "--format=%s");
        Assert.Single(log.Output.Split('\n'), l => l.StartsWith("T-001:"));
        Assert.Equal("public enum QuoteStatus { Draft }\n", await File.ReadAllTextAsync(Path.Combine(integration, NewFile)));

        // a third run is a no-op on tasks: everything replays
        model.Enqueue();
        var again = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, handover), new ApprovingHuman(), default);
        Assert.Equal("merged", again?.Status);
        Assert.Single(h.Rows(LedgerKinds.TaskEvent).Where(e => e.TaskId == "T-001"), e => e.Outcome == "Passed");
        Assert.Single((await h.GitIn(integration, "log", "--format=%s")).Output.Split('\n'), l => l.StartsWith("T-001:"));
    }
}
