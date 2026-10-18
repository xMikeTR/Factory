using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Quality;
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
            SubmitPlan(PlanFor(h.Handover)), Say("submitted"),          // planner
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
    public async Task A_frozen_acceptance_spec_missing_from_the_worktree_fails_the_run_before_any_check_runs()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        var frozen = new Handover(h.Handover.Id, h.Handover.Digest, h.Handover.Elements, h.Handover.Decisions, h.Handover.ProtectedPaths,
            h.Handover.EventVocabulary, h.Handover.Relationships,
            Microsoft.FSharp.Collections.ListModule.OfSeq([new AcceptanceSpec("tests/acceptance/quotes-db/AC-1.spec.ts", "deadbeefdeadbeef")]),
            h.Handover.WorkItem, h.Handover.DesignTokens,
            h.Handover.DesignVersion, h.Handover.AcceptanceCriteria, h.Handover.LoopSpecs);
        var path = Path.Combine(h.StateDir, "frozen-handover.json");
        Directory.CreateDirectory(h.StateDir);
        await File.WriteAllTextAsync(path, Core.HandoverJson(frozen));
        model.Enqueue(
            SubmitPlan(PlanFor(frozen)), Say("submitted"),
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services),
            new StartRun(TeamHarness.RunId, path), new ApprovingHuman(), default);

        Assert.Equal("integration_failed", result!.Status);
        Assert.Contains("tests/acceptance/quotes-db/AC-1.spec.ts is frozen in the handover but missing", result.Detail);
        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Outcome == "acceptance_specs_modified");
        Assert.DoesNotContain(h.Rows(LedgerKinds.Verify), e => e.Stage == "integration_verify" && e.Outcome is "passed" or "failed");
    }

    [Fact]
    public async Task The_pull_request_says_who_reviewed_and_carries_the_advice()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), mergePolicy: Policy(MergeEnforcement.Auto));
        h.Reviewer.Independence = Independence.SameFamily;
        h.Reviewer.Then(new Finding(FindingCategory.Design, NewFile, 1, "name it QuoteState"));
        model.Enqueue(
            SubmitPlan(PlanFor(h.Handover)), Say("submitted"),
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));

        await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default);

        var prBody = await File.ReadAllTextAsync(Path.Combine(h.StateDir, "runs", TeamHarness.RunId, $"factory_{TeamHarness.RunId}_integration.pr.md"));
        Assert.Contains("independence: same-family", prBody);
        Assert.Contains($"T-001 [Design] {NewFile}:1: name it QuoteState", prBody);
    }

    /// live2-live5: free-text plans broke on raw newlines and every rejection cost a fresh turn.
    [Fact]
    public async Task The_planner_fixes_a_rejected_plan_inside_one_turn_through_submit_plan()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), mergePolicy: Policy(MergeEnforcement.Auto));
        var outside = PlanFor(h.Handover).Replace("\"src/Pricing.Data/**\"", "\"src/Elsewhere/**\"");
        model.Enqueue(
            SubmitPlan(outside), SubmitPlan(PlanFor(h.Handover)), Say("submitted"),
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services),
            new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default);

        Assert.Equal("merged", result?.Status);
        Assert.Single(h.Rows(LedgerKinds.Plan), e => e.Outcome == "drafted");
        Assert.Single(h.Rows(LedgerKinds.Plan), e => e.Outcome == "accepted");
        var feedback = model.Requests[1].SelectMany(m => m.Contents).OfType<Microsoft.Extensions.AI.FunctionResultContent>().Single();
        Assert.Contains("src/Elsewhere/**", feedback.Result?.ToString());
        Assert.DoesNotContain(model.Requests[0], m => m.Text.Contains("## Plan schema"));
    }

    [Fact]
    public async Task A_planner_that_never_calls_submit_plan_is_told_to()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), mergePolicy: Policy(MergeEnforcement.Auto));
        model.Enqueue(Say(PlanFor(h.Handover)), SubmitPlan(PlanFor(h.Handover)), Say("submitted"));

        try { await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default); }
        catch (InvalidOperationException) { } // the script ends after planning; only the plan rows matter here

        var rejected = Assert.Single(h.Rows(LedgerKinds.Plan), e => e.Outcome == "rejected");
        Assert.Contains("submit_plan", rejected.Payload);
        Assert.Single(h.Rows(LedgerKinds.Plan), e => e.Outcome == "accepted");
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
            SubmitPlan((plan ?? (p => p))(PlanFor(h.Handover))), Say("submitted"),
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));
        return WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), human, default);
    }

    [Fact]
    public async Task Submit_approach_ledgers_the_note_and_the_validated_context_selection()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        model.Enqueue(
            SubmitPlan(PlanFor(h.Handover)), Say("submitted"),
            Call("submit_approach", new
            {
                note = "Approach: add QuoteStatus.cs",
                context = new[]
                {
                    new { path = "scripts/suite.sh", reason = "how the suite runs" },
                    new { path = "src/Pricing.Data/Missing.cs", reason = "guess" },
                },
            }), Say("done"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));

        await WorkflowHost.RunAsync(S2Workflow.Build(h.Services),
            new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default);

        Assert.Contains(model.Requests, r => r.Any(m => m.Text.Contains("### `scripts/suite.sh` (how the suite runs)")));
        var plan = h.Rows(LedgerKinds.Turn).Single(e => e.Stage == "task_plan");
        using var doc = JsonDocument.Parse(plan.Payload!);
        Assert.Equal("Approach: add QuoteStatus.cs", doc.RootElement.GetProperty("note").GetString());
        var context = doc.RootElement.GetProperty("context").EnumerateArray().ToList();
        Assert.Contains(context, c => c.GetProperty("path").GetString() == "scripts/suite.sh"
            && c.GetProperty("reason").GetString() == "how the suite runs");
        Assert.DoesNotContain(context, c => c.GetProperty("path").GetString() == "src/Pricing.Data/Missing.cs");
        Assert.Contains(doc.RootElement.GetProperty("rejected").EnumerateArray(),
            r => r.GetProperty("path").GetString() == "src/Pricing.Data/Missing.cs");
    }

    [Fact]
    public async Task A_turn_that_hits_the_tool_call_cap_goes_to_verify_with_its_work_intact()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        model.Enqueue(
            SubmitPlan(PlanFor(h.Handover)), Say("submitted"),
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }));
        for (var i = 0; i < 14; i++) model.Enqueue(Call("list_files", new { directory = "." })); // 15 calls in all, no declare_done

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services),
            new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default);

        Assert.NotEqual("failed", result?.Status);
        Assert.Equal(0, model.Remaining);
        var payloads = h.Rows(LedgerKinds.TaskEvent).Where(e => e.TaskId == "T-001").Select(e => e.Payload!);
        Assert.True(Core.Replay(new Limits(8, 3), payloads).IsPassed);
        Assert.Contains(h.Rows(LedgerKinds.Turn).Where(e => e.Stage == "act"), e => e.Payload!.Contains("\"capped\":true"));
    }

    /// A person who has not merged: the run must not claim a merge.
    private sealed class RefusingHuman : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, false, null));
    }

    /// A PR host that can auto-merge: Auto mode hands the merge to it and asks nobody.
    private sealed class AutoMergingGitHub : IPullRequests, IApprovalComments
    {
        public int AutoMerges { get; private set; }
        public Task<string> EnsurePullRequestAsync(string head, string baseBranch, string title, string body, CancellationToken ct) =>
            Task.FromResult("https://github.com/o/r/pull/1");
        public Task EnableAutoMergeAsync(string url, CancellationToken ct) { AutoMerges++; return Task.CompletedTask; }
        public Task<IReadOnlyList<PrComment>> CommentsAsync(string url, CancellationToken ct) => Task.FromResult<IReadOnlyList<PrComment>>([]);
        public Task MergeAsync(string url, CancellationToken ct) => throw new InvalidOperationException("auto mode never merges directly");
    }

    [Fact]
    public async Task Auto_mode_on_a_PR_host_enables_auto_merge_on_green_whatever_the_tier()
    {
        var model = new ScriptedChatClient();
        var host = new AutoMergingGitHub();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3),
            mergePolicy: Policy(MergeEnforcement.Auto), pullRequests: host);
        var human = new ApprovingHuman();

        var result = await RunT001(h, model, human);

        Assert.Equal("auto_merge_enabled", result?.Status);
        Assert.Equal(1, host.AutoMerges);
        Assert.Empty(human.Approvals);
        Assert.Single(h.Rows(LedgerKinds.Merge), e => e.Outcome == "TwoApprovals"); // still reported: Quotes DB is high risk
    }

    [Fact]
    public async Task Auto_mode_with_no_PR_host_asks_a_person_to_merge_the_integration_branch_and_ends_merged()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), mergePolicy: Policy(MergeEnforcement.Auto));
        var human = new ApprovingHuman();

        var result = await RunT001(h, model, human);

        Assert.Equal("merged", result?.Status);
        var request = Assert.Single(human.Approvals);
        Assert.Contains($"no pull request host: merge {GitWorkspace.IntegrationBranch(TeamHarness.RunId)} into main yourself", request.Reasons);
        Assert.Single(h.Rows(LedgerKinds.Merge), e => e.Outcome == "merged");
    }

    [Fact]
    public async Task Auto_mode_with_no_PR_host_ends_not_merged_when_the_person_has_not_merged()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), mergePolicy: Policy(MergeEnforcement.Auto));
        model.Enqueue(
            SubmitPlan(PlanFor(h.Handover)), Say("submitted"),
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new RefusingHuman(), default);

        Assert.Equal("not_merged", result?.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_pending_manual_check_blocks_the_merge_and_parks_the_run_in_both_modes(bool auto)
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), mergePolicy:
            Policy(auto ? MergeEnforcement.Auto : MergeEnforcement.NewTiered(Microsoft.FSharp.Collections.ListModule.OfSeq(["alice"]))));
        var human = new ApprovingHuman();

        var result = await RunT001(h, model, human, WithManualCheck);

        Assert.Equal("parked", result?.Status); // it did not fail: it waits on a person
        Assert.StartsWith("waiting on a manual check: T-001: eyeball the enum (Check the names read well.)", result?.Detail);
        Assert.Contains("factory-s2 verify", result?.Detail);
        Assert.Empty(human.Approvals);
        Assert.Single(h.Rows(LedgerKinds.Merge), e => e.Outcome == "blocked");
        var prBody = await File.ReadAllTextAsync(Path.Combine(h.StateDir, "runs", TeamHarness.RunId, $"factory_{TeamHarness.RunId}_integration.pr.md"));
        Assert.Contains("Merge blocked", prBody);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_manual_check_recorded_with_verify_lets_the_resumed_run_merge_and_a_deferral_reaches_the_PR(bool auto)
    {
        var model = new ScriptedChatClient();
        var enforcement = auto ? MergeEnforcement.Auto : MergeEnforcement.NewTiered(Microsoft.FSharp.Collections.ListModule.OfSeq(["alice"]));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), mergePolicy: Policy(enforcement));
        var human = new ApprovingHuman();
        var parked = await RunT001(h, model, human, WithManualCheck);
        Assert.Equal("parked", parked?.Status);
        const string check = "T-001: eyeball the enum";

        Assert.Equal($"no manual check 'T-001: nope' in run {TeamHarness.RunId} (T-001: eyeball the enum)",
            await ManualChecks.RecordAsync(h.Ledger, TeamHarness.RunId, h.Services.LoadDispatch(TeamHarness.RunId), enforcement, "T-001: nope", "alice", "done", null, default));
        Assert.Equal("a deferred check needs --note saying why",
            await ManualChecks.RecordAsync(h.Ledger, TeamHarness.RunId, h.Services.LoadDispatch(TeamHarness.RunId), enforcement, check, "alice", "deferred", " ", default));
        if (!auto)
            Assert.Equal("mallory is not a merge approver (alice)",
                await ManualChecks.RecordAsync(h.Ledger, TeamHarness.RunId, h.Services.LoadDispatch(TeamHarness.RunId), enforcement, check, "mallory", "done", null, default));
        Assert.Null(await ManualChecks.RecordAsync(h.Ledger, TeamHarness.RunId, h.Services.LoadDispatch(TeamHarness.RunId), enforcement,
            check, "alice", "deferred", "no Godot project yet", default));

        var resumed = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), human, default);

        Assert.Equal("merged", resumed?.Status); // no PR host: a person merges in both modes
        Assert.Equal(0, model.Remaining); // nothing ran again but the merge
        var prBody = await File.ReadAllTextAsync(Path.Combine(h.StateDir, "runs", TeamHarness.RunId, $"factory_{TeamHarness.RunId}_integration.pr.md"));
        Assert.Contains("Manual checks deferred", prBody);
        Assert.Contains("- T-001: eyeball the enum, deferred by alice: no Godot project yet", prBody);
        Assert.Contains("\"deferred\":[\"T-001: eyeball the enum, deferred by alice: no Godot project yet\"]", h.Rows(LedgerKinds.Merge).Last(e => e.Payload!.Contains("\"tier\"")).Payload);
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

    private static void RunScript(ScriptedChatClient model, Handover h) => model.Enqueue(
        SubmitPlan(PlanFor(h)), Say("submitted"),
        Say("Approach: add QuoteStatus.cs"),
        Call("declare_done", new { summary = "test already on main" }), Say("done"),
        Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
        Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));

    private static QaTest QaT(string id, QaKind kind, QaOutcome baseline, QaOutcome integrated) => new(id, "R1", kind, baseline, integrated);

    [Fact]
    public async Task A_QA_escape_ends_the_run_unmerged_with_the_regression_named()
    {
        var model = new ScriptedChatClient();
        var qa = new ScriptedQaRunner(new QaRun(["R1"], [QaT("n", QaKind.New, QaOutcome.QaFailed, QaOutcome.QaPassed), QaT("c", QaKind.Characterisation, QaOutcome.QaPassed, QaOutcome.QaFailed)], false));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), qa: qa);
        RunScript(model, h.Handover);

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default);

        Assert.Equal("qa_failed", result?.Status);
        Assert.Contains("ESCAPE c", result?.Detail);
        Assert.Single(qa.Asked);
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "qa" && e.Outcome == "failed");
    }

    [Fact]
    public async Task A_QA_run_that_never_finished_is_not_a_pass()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), qa: new ScriptedQaRunner(new QaRun([], [], true)));
        RunScript(model, h.Handover);

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default);

        Assert.Equal("qa_unrunnable", result?.Status);
    }

    [Fact]
    public async Task A_clean_QA_judgement_lets_the_run_reach_the_merge()
    {
        var model = new ScriptedChatClient();
        var qa = new ScriptedQaRunner(new QaRun(["R1"], [QaT("n", QaKind.New, QaOutcome.QaFailed, QaOutcome.QaPassed)], false));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), qa: qa);
        RunScript(model, h.Handover);

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default);

        Assert.NotEqual("qa_failed", result?.Status);
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Stage == "qa" && e.Outcome == "passed");
    }

    /// 20261016-095826: no solution at the repo root and no .venv in the image. Nothing ran against the
    /// code, so the run does not say the code failed.
    [Fact]
    public async Task Integration_checks_the_sandbox_could_not_run_end_the_run_unrunnable_not_failed()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), suiteCommand: "exit 127");
        RunScript(model, h.Handover);

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default);

        Assert.Equal("integration_unrunnable", result?.Status);
        Assert.Contains("integration tests", result!.Detail);
        Assert.Contains("--run-id", result.Detail);
    }

    /// A new repo names no suite and has no test project yet: `dotnet test` at its root would be MSB1003. There is
    /// nothing to run, which is not the code failing; the task's own red-green checks have already gated it.
    [Fact]
    public async Task A_repo_with_no_suite_named_and_no_test_project_yet_integrates_with_no_suite()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), suiteCommand: null);
        RunScript(model, h.Handover);

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new ApprovingHuman(), default);

        Assert.Equal("merged", result?.Status);
        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Stage == "integration_verify" && e.Outcome == "no_suite");
    }

    [Fact]
    public async Task A_finished_run_projects_its_tasks_and_decision_checks_into_the_registry_ledger()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        Directory.CreateDirectory(h.Services.Settings.RegistryPath); // empty registry: D-0001's P check fails there
        model.Enqueue(
            SubmitPlan(PlanFor(h.Handover)), Say("submitted"),
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
        Assert.False(File.Exists(Path.Combine(ledger, "designc.jsonl"))); // not merged: the handover stays open for a resume
    }

    [Fact]
    public async Task A_needs_decision_parks_the_run_until_designc_resolves_it_then_resumes_to_Passed()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        var registry = h.Services.Settings.RegistryPath;
        Directory.CreateDirectory(registry);
        model.Enqueue(
            SubmitPlan(PlanFor(h.Handover)), Say("submitted"),
            Call("submit_approach", new { note = "Approach: add QuoteStatus.cs", context = new[] { new { path = "scripts/suite.sh", reason = "how the suite runs" } } }), Say("done"),
            Call("raise_needs_decision", new { question = "Is Draft the only initial status?", options = new[] { new { id = "A", summary = "Draft only", consequence = "one initial status" }, new { id = "B", summary = "Draft and Bound", consequence = "two initial statuses" } }, recommended = "A" }), Say("blocked"));
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
        var options = nd.GetProperty("payload").GetProperty("options");
        Assert.Equal(["A", "B"], options.EnumerateArray().Select(o => o.GetProperty("id").GetString()));
        Assert.Equal("Draft and Bound", options[1].GetProperty("summary").GetString());
        Assert.Equal("A", nd.GetProperty("payload").GetProperty("recommended").GetString());

        // Still unresolved: a re-run parks again on the same id without asking the model anything.
        var again = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, handover), new ApprovingHuman(), default);
        Assert.Equal("parked: ND-0001", RunReport.Line(again!));
        Assert.Equal(0, model.Remaining);

        // A resolution naming an option that was never offered keeps the run parked, and says why.
        var designc = Path.Combine(registry, "ledger", "designc.jsonl");
        await File.WriteAllTextAsync(designc,
            """{"kind":"needs_decision_resolved","id":"ND-0001","by":"designer","schema_version":1,"event_id":"e-1","ts":1791500000000,"payload":{"option":"Z"}}""" + "\n");
        var wrong = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, handover), new ApprovingHuman(), default);
        Assert.Equal("parked", wrong?.Status);
        Assert.Contains("ND-0001", wrong?.Detail);
        Assert.Contains("'Z' is not one of the options (A, B)", wrong?.Detail);

        await File.WriteAllTextAsync(designc,
            """{"kind":"needs_decision_resolved","id":"ND-0001","by":"designer","schema_version":1,"event_id":"e-2","ts":1791500000001,"payload":{"option":"B","note":"Bound comes from imports"}}""" + "\n");
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
        // The planner did not run again: the nominated file comes from the selection frozen in the ledger.
        Assert.Contains(model.Requests.Last(), m => m.Text.Contains("### `scripts/suite.sh` (how the suite runs)"));
        Assert.Contains(model.Requests.Last(), m => m.Text.Contains("### `src/Pricing.Data/Quote.cs`"));
        Assert.Contains(model.Requests.Last(), m => m.Text.Contains("Chosen option B: Draft and Bound") && m.Text.Contains("Bound comes from imports"));
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
            SubmitPlan(PlanFor(h.Handover)), Say("submitted"),
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
