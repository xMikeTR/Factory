using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Factory.Integration.Tests.Workflow;
using Factory.S2;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Generation;

/// The whole run with a real (fake-generated) contract: generation before the planner, seeding, and drift in integration verify.
public sealed class GeneratedCodeDriftWorkflowTests
{
    private const string Stem = "generated/contracts/csharp/FareQuotes/FareQuotes";

    private sealed class Approver : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "t"));
    }

    private static async Task<(TeamHarness H, RunFinished? Result)> RunAsync(Action<string, TeamHarness>? tamper)
    {
        var probe = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: new FakeCodeGenerator(),
            shapeHandover: b => b.WithContract(b.Elements.Head.Id.Value, "contracts/openapi/fare-quotes.yaml", "openapi: 3.0.0\n"));
        // A harness fixes its model at creation: rebuild with the script now the shaped handover is known.
        var h = probe;
        var plan = S2WorkflowTests.PlanFor(h.Handover);
        h.Dispose();
        var model = new ScriptedChatClient();
        model.Enqueue(
            SubmitPlan(plan), Say("submitted"), Say("Approach: add BookingStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = "src/Fares.Data/BookingStatus.cs", content = "public enum BookingStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added BookingStatus" }), Say("done"));
        h = await TeamHarness.CreateAsync(model, new Limits(8, 3), codegen: new FakeCodeGenerator(),
            shapeHandover: b => b.WithContract(b.Elements.Head.Id.Value, "contracts/openapi/fare-quotes.yaml", "openapi: 3.0.0\n"));
        var touched = false;
        h.Ledger.OnAppend = e =>
        {
            if (touched || tamper is null || e.Outcome != "merged_to_integration") return;
            touched = true;
            tamper(h.Services.Git.IntegrationPath(TeamHarness.RunId), h);
        };
        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new Approver(), default);
        return (h, result);
    }

    [Fact]
    public async Task Generation_precedes_planning_and_the_files_are_on_the_integration_branch()
    {
        var (h, result) = await RunAsync(null);
        using var _ = h;
        Assert.Equal("ready_for_review", result?.Status); // a clean run: seeded files are not judged by the protected-path rule
        Assert.DoesNotContain(h.Ledger.Events, e => (e.Payload ?? "").Contains("generated/contracts") && e.Kind == LedgerKinds.Merge);
        var kinds = h.Ledger.Events.Select(e => e.Kind).ToList();
        Assert.True(kinds.IndexOf(LedgerKinds.Codegen) < kinds.IndexOf(LedgerKinds.Plan), string.Join(",", kinds));
        Assert.Equal("generated", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
        Assert.True(File.Exists(Path.Combine(h.Services.Git.IntegrationPath(TeamHarness.RunId), $"{Stem}.Dtos.g.cs")));
        Assert.DoesNotContain(h.Rows(LedgerKinds.Verify), e => e.Outcome == "generated_code_modified");
    }

    [Fact]
    public async Task An_edit_to_a_generated_file_ends_the_run_integration_failed_with_generated_code_modified()
    {
        var (h, result) = await RunAsync((integration, _) => File.AppendAllText(Path.Combine(integration, $"{Stem}.Dtos.g.cs"), "// agent edit\n"));
        using var _ = h;
        Assert.Equal("integration_failed", result?.Status);
        Assert.Contains($"modified: {Stem}.Dtos.g.cs", result!.Detail);
        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Outcome == "generated_code_modified");
    }

    [Fact]
    public void Generated_contract_code_is_a_protected_path()
    {
        Assert.Contains("generated/contracts/**", S2Config.Default.ProtectedPaths);
    }
}
