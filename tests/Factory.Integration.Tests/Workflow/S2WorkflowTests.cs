using System.Text.Json;
using System.Text.Json.Nodes;
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
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct)
        {
            Approvals.Add(request);
            return Task.FromResult(new MergeApprovalResponse(request.RunId, true, "test-approver"));
        }
    }

    internal static string PlanFor(Handover h) => JsonSerializer.Serialize(new
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
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), mergePolicy: Policy(MergeEnforcement.NewTiered(Microsoft.FSharp.Collections.ListModule.Empty<string>())));
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

    private static MergePolicy Policy(MergeEnforcement enforcement) =>
        new(enforcement, Core.DefaultMergePolicy.AutoMergeMaxRisk, Core.DefaultMergePolicy.AutoMergeMaxChangedLines);

    private static string WithManualCheck(string plan)
    {
        var json = JsonNode.Parse(plan)!;
        json["tasks"]![0]!["verify"]!.AsArray().Add(new JsonObject
        {
            ["name"] = "eyeball the enum", ["kind"] = "manual", ["instructions"] = "Check the names read well.",
        });
        return json.ToJsonString();
    }

    private static Task<RunFinished?> RunT001(TeamHarness h, ScriptedChatClient model, ApprovingHuman human, Func<string, string>? plan = null)
    {
        model.Enqueue(
            Say((plan ?? (p => p))(PlanFor(h.Handover))),
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));
        return WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), human, default);
    }

    [Fact]
    public async Task Auto_mode_merges_on_green_whatever_the_tier()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3),
            mergePolicy: Policy(MergeEnforcement.Auto));
        var human = new ApprovingHuman();

        var result = await RunT001(h, model, human);

        Assert.Equal("auto_merge_enabled", result?.Status);
        Assert.Empty(human.Approvals);
        Assert.Single(h.Rows(LedgerKinds.Merge), e => e.Outcome == "TwoApprovals"); // still reported: Quotes DB is high risk
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_pending_manual_check_blocks_the_merge_in_both_modes(bool auto)
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), mergePolicy:
            Policy(auto ? MergeEnforcement.Auto : MergeEnforcement.NewTiered(Microsoft.FSharp.Collections.ListModule.OfSeq(["alice"]))));
        var human = new ApprovingHuman();

        var result = await RunT001(h, model, human, WithManualCheck);

        Assert.Equal("merge_blocked", result?.Status);
        Assert.Contains("manual check pending: T-001: eyeball the enum", result?.Detail);
        Assert.Empty(human.Approvals);
        Assert.Single(h.Rows(LedgerKinds.Merge), e => e.Outcome == "blocked");
        var prBody = await File.ReadAllTextAsync(Path.Combine(h.StateDir, "runs", TeamHarness.RunId, $"factory_{TeamHarness.RunId}_integration.pr.md"));
        Assert.Contains("Merge blocked", prBody);
    }

    /// A GitHub stand-in: PR comments are whatever the test posts; merges are counted.
    private sealed class FakeGitHub : IPullRequests, IApprovalComments
    {
        public List<PrComment> Comments { get; } = [];
        public int Merges { get; private set; }
        public Task<string> EnsurePullRequestAsync(string head, string baseBranch, string title, string body, CancellationToken ct) =>
            Task.FromResult("https://github.com/o/r/pull/1");
        public Task EnableAutoMergeAsync(string url, CancellationToken ct) => throw new InvalidOperationException("tiered mode never auto-merges");
        public Task<IReadOnlyList<PrComment>> CommentsAsync(string url, CancellationToken ct) => Task.FromResult<IReadOnlyList<PrComment>>(Comments.ToList());
        public Task MergeAsync(string url, CancellationToken ct) { Merges++; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Tiered_mode_parks_awaiting_approve_comments_and_a_single_operator_merges_with_two_reasons()
    {
        var model = new ScriptedChatClient();
        var github = new FakeGitHub();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), pullRequests: github,
            mergePolicy: Policy(MergeEnforcement.NewTiered(Microsoft.FSharp.Collections.ListModule.OfSeq(["mike"]))));
        var human = new ApprovingHuman();

        var waiting = await RunT001(h, model, human);

        Assert.Equal("parked", waiting?.Status); // TwoApprovals (Quotes DB is high risk); the handover stays open
        Assert.Equal("parked: awaiting_approval https://github.com/o/r/pull/1 (2 more /approve from mike)", RunReport.Line(waiting!));
        Assert.Empty(human.Approvals);
        Assert.Equal(0, github.Merges);

        github.Comments.Add(new PrComment("mallory", "/approve"));                 // not allow-listed
        github.Comments.Add(new PrComment("mike", "/approve schema reviewed"));
        github.Comments.Add(new PrComment("mike", "/approve migration dry-run ok"));
        model.Enqueue();
        var merged = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), human, default);

        Assert.Equal("merged", merged?.Status);
        Assert.Equal(1, github.Merges);
        var row = h.Rows(LedgerKinds.Merge).Last();
        Assert.Equal("merged", row.Outcome);
        using var payload = JsonDocument.Parse(row.Payload!);
        Assert.True(payload.RootElement.GetProperty("single_operator").GetBoolean());
        Assert.Equal(["mike"], payload.RootElement.GetProperty("by").EnumerateArray().Select(x => x.GetString()));
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
        Assert.All(pipeline.Concat(checks), l => Assert.True(DesigncSchema.KeptByGate(l), l));
        Assert.Single(pipeline, l => l.Contains("\"task\":\"T-001\"") && l.Contains("\"status\":\"passed\""));
        var check = JsonDocument.Parse(Assert.Single(checks)).RootElement;
        Assert.Equal("D-0001", check.GetProperty("decision").GetString());
        Assert.Equal("p check -tc tcOptimistic", check.GetProperty("check").GetString());
        Assert.Equal("fail", check.GetProperty("outcome").GetString());
        Assert.NotEqual(0, check.GetProperty("exit_code").GetInt32());
        var closed = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(Path.Combine(ledger, "designc.jsonl")))).RootElement;
        Assert.Equal("handover_closed", closed.GetProperty("kind").GetString());
        Assert.Equal(h.Handover.Id, closed.GetProperty("handover").GetString());
        Assert.Equal("abandoned", closed.GetProperty("outcome").GetString()); // D-0001's check failed
    }

    [Fact]
    public async Task A_needs_decision_parks_the_run_until_designc_resolves_it_then_resumes_to_Passed()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        var registry = h.Services.Settings.RegistryPath;
        Directory.CreateDirectory(registry);
        model.Enqueue(
            Say(PlanFor(h.Handover)),
            Say("Approach: add QuoteStatus.cs"),
            Call("raise_needs_decision", new { question = "Is Draft the only initial status?" }), Say("blocked"));
        var handover = h.WriteHandoverJson();

        var parked = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, handover), new ApprovingHuman(), default);

        Assert.Equal("parked", parked?.Status);
        Assert.Equal("ND-0001", parked?.Detail);
        Assert.Equal("parked: ND-0001", RunReport.Line(parked!));
        var nd = JsonDocument.Parse(Assert.Single(await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "pipeline.jsonl")),
            l => l.Contains("\"kind\":\"needs_decision\""))).RootElement;
        Assert.Equal("ND-0001", nd.GetProperty("id").GetString());
        Assert.Equal("open", nd.GetProperty("status").GetString()); // designc's gate raises "open needs-decision ND-0001"
        Assert.True(DesigncSchema.KeptByGate(nd.GetRawText()));
        Assert.Equal("T-001", nd.GetProperty("task").GetString());

        // Still unresolved: a re-run parks again on the same id without asking the model anything.
        var again = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, handover), new ApprovingHuman(), default);
        Assert.Equal("parked: ND-0001", RunReport.Line(again!));
        Assert.Equal(0, model.Remaining);

        await File.WriteAllTextAsync(Path.Combine(registry, "ledger", "designc.jsonl"),
            """{"kind":"needs_decision_resolved","id":"ND-0001","by":"designer","schema_version":1,"event_id":"e-1","ts":1791500000000}""" + "\n");
        model.Enqueue( // the approach note is replayed, so no task-planner turn
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));

        await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, handover), new ApprovingHuman(), default);

        Assert.Equal(0, model.Remaining);
        var payloads = h.Rows(LedgerKinds.TaskEvent).Where(e => e.TaskId == "T-001").Select(e => e.Payload!);
        Assert.True(Core.Replay(new Limits(8, 3), payloads).IsPassed);
        Assert.Contains(model.Requests.Last(), m => m.Text.Contains("ND-0001"));
        var pipeline = await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "pipeline.jsonl"));
        Assert.Single(pipeline, l => l.Contains("\"kind\":\"needs_decision\"") && l.Contains("\"status\":\"resolved\""));
        Assert.Single(pipeline, l => l.Contains("\"task\":\"T-001\"") && l.Contains("\"status\":\"passed\""));
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

        Assert.Equal("auto_merge_enabled", result?.Status);
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
        Assert.Equal("auto_merge_enabled", again?.Status);
        Assert.Single(h.Rows(LedgerKinds.TaskEvent).Where(e => e.TaskId == "T-001"), e => e.Outcome == "Passed");
        Assert.Single((await h.GitIn(integration, "log", "--format=%s")).Output.Split('\n'), l => l.StartsWith("T-001:"));
    }
}
