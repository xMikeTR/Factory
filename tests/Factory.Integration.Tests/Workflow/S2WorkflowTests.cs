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
                dependsOn = Array.Empty<string>(),
                covers = new[] { "D-0001" },
                verify = new[] { new { name = "file exists", kind = "invariant", run = $"test -f {NewFile}", timeoutSeconds = 30 } },
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
}
