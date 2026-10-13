using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Workflow;

using Core = Factory.Core.Core;

/// The regeneration drill: a decision changes in Designc, the handover is re-frozen, and a run
/// `--since` the previous one re-runs only the tasks on the elements that decision binds.
public class RegenerationTests
{
    private const string PreviousRun = "run-prev";

    private sealed class ApprovingHuman : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "test-approver"));
    }

    /// T-001 on Quotes DB proves D-0001; T-002 on Pricing API is bound by no decision.
    private static string PlanFor(Handover h) => JsonSerializer.Serialize(new
    {
        handover = new { id = h.Id, digest = h.Digest },
        tasks = new object[]
        {
            new
            {
                id = "T-001", title = "Add QuoteStatus", instructions = "Add the QuoteStatus enum.",
                elements = new[] { "Pricing Platform/Quotes DB" },
                writes = new[] { "src/Pricing.Data/**" }, testWrites = new[] { "tests/Pricing.Data.Tests/**" },
                dependsOn = Array.Empty<string>(), covers = new[] { "D-0001" },
                verify = new[] { new { name = "file exists", kind = "invariant", run = $"sh {TeamHarness.QuoteStatusTest}", timeoutSeconds = 30 } },
            },
            new
            {
                id = "T-002", title = "Expose quote status", instructions = "Return the status from the API.",
                elements = new[] { "Pricing Platform/Pricing API" },
                writes = new[] { "src/Pricing.Api/**" }, testWrites = new[] { "tests/Pricing.Api.Tests/**" },
                dependsOn = new[] { "T-001" }, covers = Array.Empty<string>(),
                verify = new[] { new { name = "api exists", kind = "invariant", run = "sh tests/Pricing.Api.Tests/status.sh", timeoutSeconds = 30 } },
            },
        },
    });

    [Fact]
    public async Task A_changed_decision_re_runs_only_the_bound_elements_tasks()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));

        // The previous run, as it left its run directory: the handover it ran and its plan.
        var previousDir = Directory.CreateDirectory(Path.Combine(h.StateDir, "runs", PreviousRun)).FullName;
        await File.WriteAllTextAsync(Path.Combine(previousDir, "handover.json"), Core.HandoverJson(h.Handover));
        await File.WriteAllTextAsync(Path.Combine(previousDir, "plan.json"), PlanFor(h.Handover));

        // Designc: D-0001's statement changes and the handover is re-frozen (new digest).
        var before = h.Handover;
        var refrozen = new Handover(before.Id, "sha256:refrozen", before.Elements,
            Core.ToList(before.Decisions.Select(d => new BoundDecision(d.Id, d.Statement + " Retry once on conflict.", d.Binds, d.Checks))),
            before.ProtectedPaths, before.EventVocabulary, before.Relationships, before.AcceptanceSpecs, before.WorkItem);
        var handoverPath = Path.Combine(h.StateDir, "refrozen.json");
        await File.WriteAllTextAsync(handoverPath, Core.HandoverJson(refrozen));

        // No planner call: the previous plan is reused. Only T-001's turns are scripted.
        model.Enqueue(
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = "src/Pricing.Data/QuoteStatus.cs", content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }),
            Say("done"));

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services),
            new StartRun(TeamHarness.RunId, handoverPath, Since: PreviousRun), new ApprovingHuman(), default);

        Assert.True(result?.Status == "auto_merge_enabled", $"{result?.Status}: {result?.Detail}"); // the default policy, auto
        Assert.Equal(0, model.Remaining);

        var regeneration = Assert.Single(h.Rows(LedgerKinds.Plan), e => e.Stage == "regenerate");
        Assert.Equal("1 of 2 tasks re-run", regeneration.Outcome);
        Assert.Contains("D-0001", regeneration.Payload);

        Assert.Contains(h.Rows(LedgerKinds.TaskEvent), e => e.TaskId == "T-001");
        Assert.DoesNotContain(h.Rows(LedgerKinds.TaskEvent), e => e.TaskId == "T-002");
        Assert.DoesNotContain(h.Rows(LedgerKinds.Turn), e => e.TaskId == "T-002");
        Assert.Single(h.Rows(LedgerKinds.Dispatch), e => e.TaskId == "T-002" && e.Outcome == "carried");
    }
}
