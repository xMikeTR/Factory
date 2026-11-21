using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Workflow;

using Core = Factory.Core.Core;

/// The whole integration executor driven through UI drift (no Docker): an approved picture touched on the integration branch ends the run
/// integration_failed with ui_baselines_modified, and no ui: check runs (nothing reaches the page tests, the suite or the sandbox).
public sealed class UiDriftWorkflowTests
{
    private sealed class ApprovingHuman : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "test-approver"));
    }

    [Fact]
    public async Task A_touched_approved_picture_ends_the_run_integration_failed_and_no_ui_check_runs()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        var ui = UiHandoverDir.Create();
        var uiJson = await HandoverInput.ResolveAsync(ui.Dir, h.StateDir, default);
        var uiHandover = Core.ParseHandover(await File.ReadAllTextAsync(uiJson));
        var b = h.Handover;
        var handover = new Handover(b.Id, b.Digest, b.Elements, b.Decisions, b.ProtectedPaths, b.EventVocabulary, b.Relationships, b.AcceptanceSpecs,
            b.WorkItem, b.DesignTokens, b.DesignVersion, b.AcceptanceCriteria, b.LoopSpecs, b.References, b.Contracts, b.Uncovered, b.ApiContracts,
            b.RequirementRisk, b.RequirementDataClass, uiHandover.Ui, b.Tests, b.ScenarioTests, b.ScenarioSkipped, b.Interfaces, b.ApiContractTexts, null, b.ApiScenarios, b.Entities);
        var json = Path.Combine(h.StateDir, "handovers", "ui-drift.json");
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        await File.WriteAllTextAsync(json, Core.HandoverJson(handover));
        await HandoverInput.CopyUiPreviewsAsync(handover, ui.Dir, HandoverInput.PreviewsFolder(json), default);

        model.Enqueue(
            SubmitPlan(S2WorkflowTests.PlanFor(handover)), Say("submitted"),
            Say("Approach: add BookingStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = "src/Fares.Data/BookingStatus.cs", content = "public enum BookingStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added BookingStatus" }), Say("done"));

        // The seed happens once the plan is dispatched; the team's merge into integration follows it, still before integration verify.
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        var png = Path.Combine(integration, "ui-contract", "previews", "bordereaux", "ready-chromium-linux.png");
        var touched = false;
        h.Ledger.OnAppend = e =>
        {
            if (touched || e.Outcome != "merged_to_integration") return;
            touched = true;
            File.AppendAllText(png, "touched");
        };

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, json), new ApprovingHuman(), default);

        Assert.True(touched, $"never touched: {result?.Status} {result?.Detail} :: " + string.Join(",", h.Ledger.Events.Select(e => e.Kind + "/" + e.Stage + "/" + e.Outcome)));
        Assert.Equal("integration_failed", result?.Status);
        Assert.Contains("ready-chromium-linux.png", result!.Detail);
        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Outcome == "ui_baselines_modified");
        // Judged failed by drift alone: one fail row per screen; no check ran (no per-state failures, no could_not_run or pass, no suite).
        var verdict = Assert.Single(h.Rows(LedgerKinds.UiResult));
        Assert.Equal("fail", verdict.Outcome);
        Assert.Empty(h.Rows(LedgerKinds.UiCheckFailed));
        Assert.DoesNotContain(h.Rows(LedgerKinds.Verify), e => e.Stage == "integration_verify" && e.Outcome is "passed" or "failed");
    }
}
