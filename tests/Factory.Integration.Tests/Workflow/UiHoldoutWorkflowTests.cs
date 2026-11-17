using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

/// The holdout through the whole integration executor, without Docker (FakeUiSandboxes answers the page and holdout runs).
public sealed class UiHoldoutWorkflowTests
{
    private sealed class ApprovingHuman : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "test-approver"));
    }

    private static JsonElement P(LedgerEvent e) => JsonDocument.Parse(e.Payload!).RootElement;

    private static async Task<(UiWorkflowSetup Setup, RunFinished? Result)> RunAsync(UiHandoverDir ui, Action<UiWorkflowSetup>? arrange = null, bool holdoutInRegistry = true, Factory.S2.Quality.IQaRunner? qa = null)
    {
        var model = new ScriptedChatClient();
        var s = await UiWorkflowSetup.CreateAsync(model, ui, holdoutInRegistry, qa: qa);
        UiWorkflowSetup.Script(model, s.Handover);
        arrange?.Invoke(s);
        var result = await WorkflowHost.RunAsync(S2Workflow.Build(s.Harness.Services), new StartRun(TeamHarness.RunId, s.HandoverJson), new ApprovingHuman(), default);
        if (result?.Status == "plan_rejected") throw new Xunit.Sdk.XunitException(result.Detail);
        return (s, result);
    }

    [Fact]
    public async Task A_holdout_that_changed_since_the_freeze_is_could_not_run_and_the_run_is_unrunnable()
    {
        var (s, result) = await RunAsync(UiHandoverDir.Create(holdoutSha: new string('a', 64)));

        Assert.Equal("integration_unrunnable", result?.Status);
        Assert.Empty(s.Sandboxes.HoldoutRuns);
        var verify = Assert.Single(s.Harness.Rows(LedgerKinds.Verify), e => e.Payload!.Contains("\"holdout\""));
        Assert.Equal("failed", verify.Outcome);
        Assert.Contains("holdout changed since the freeze", result!.Detail);
        var ui = Assert.Single(s.Harness.Rows(LedgerKinds.UiResult));
        Assert.Equal("could_not_run", ui.Outcome);
    }

    [Fact]
    public async Task A_missing_registry_file_is_could_not_run()
    {
        var (s, result) = await RunAsync(UiHandoverDir.Create(), holdoutInRegistry: false);

        Assert.Equal("integration_unrunnable", result?.Status);
        Assert.Contains("not found", result!.Detail);
        Assert.Equal("could_not_run", Assert.Single(s.Harness.Rows(LedgerKinds.UiResult)).Outcome);
    }

    [Fact]
    public async Task A_screen_with_no_holdout_is_coverage_not_a_failure_and_the_run_passes_on()
    {
        var (s, result) = await RunAsync(UiHandoverDir.Create(holdout: false));

        Assert.True(result?.Status == "merge_blocked", result?.Status + ": " + result?.Detail);
        Assert.Contains("is under a protected path", result!.Detail); // the seeded ui-contract files are the Factory's: the run got past integration verify to the merge gate
        var row = Assert.Single(s.Harness.Rows(LedgerKinds.Verify), e => e.Outcome == "no_holdout");
        Assert.Equal("bordereaux", P(row).GetProperty("screen").GetString());
        Assert.Empty(s.Sandboxes.HoldoutRuns);
        Assert.Equal("pass", Assert.Single(s.Harness.Rows(LedgerKinds.UiResult)).Outcome);
    }

    [Fact]
    public async Task A_passing_holdout_runs_in_a_scratch_copy_that_is_gone_afterwards_and_ui_result_passes()
    {
        var (s, result) = await RunAsync(UiHandoverDir.Create());

        Assert.True(result?.Status == "merge_blocked", result?.Status + ": " + result?.Detail);
        Assert.Contains("is under a protected path", result!.Detail); // the seeded ui-contract files are the Factory's: the run got past integration verify to the merge gate
        var run = Assert.Single(s.Sandboxes.HoldoutRuns);
        Assert.True(run.Test && run.Sample);
        Assert.Contains("--project ui-holdout", run.Command);
        Assert.False(Directory.Exists(run.Workdir));
        Assert.NotEqual(s.Harness.Services.Git.IntegrationPath(TeamHarness.RunId), run.Workdir);
        var row = Assert.Single(s.Harness.Rows(LedgerKinds.Verify), e => e.Payload!.Contains("\"holdout\""));
        Assert.Equal("passed", row.Outcome);
        Assert.Equal("pass", Assert.Single(s.Harness.Rows(LedgerKinds.UiResult)).Outcome);
        // The generated test and the sample never reached the integration branch.
        Assert.False(File.Exists(Path.Combine(s.Harness.Services.Git.IntegrationPath(TeamHarness.RunId), "ui-contract", "tests", "bordereaux.holdout.test.tsx")));
    }

    [Fact]
    public async Task A_failing_state_gives_one_holdout_failed_row_per_blamed_task_with_screen_and_state_only_and_no_runner_text_in_any_row()
    {
        var (s, result) = await RunAsync(UiHandoverDir.Create(), a => a.Sandboxes.HoldoutFailing.Add("ready"));

        Assert.Equal("integration_failed", result?.Status);
        var row = Assert.Single(s.Harness.Rows(LedgerKinds.HoldoutFailed));
        Assert.Equal("T-001", row.TaskId);
        Assert.Equal("{\"screen\":\"bordereaux\",\"state\":\"ready\"}", row.Payload);
        Assert.Equal("fail", Assert.Single(s.Harness.Rows(LedgerKinds.UiResult)).Outcome);
        var all = string.Join("\n", s.Harness.Ledger.Events.Select(e => e.Payload));
        Assert.DoesNotContain("SECRET-ROW-DATA", all + result!.Detail);
        Assert.Contains("holdout_failed: bordereaux/ready", result.Detail);
    }

    [Fact]
    public async Task The_holdout_runs_before_QA_and_a_failing_holdout_means_QA_never_runs()
    {
        var qa = new ScriptedQaRunner(new Factory.S2.Quality.QaRun([], [], false));
        var (s, result) = await RunAsync(UiHandoverDir.Create(), a => a.Sandboxes.HoldoutFailing.Add("empty"), qa: qa);
        Assert.Equal("integration_failed", result?.Status);
        Assert.Empty(qa.Asked);

        var qa2 = new ScriptedQaRunner(new Factory.S2.Quality.QaRun([], [], false));
        var (s2, _) = await RunAsync(UiHandoverDir.Create(), qa: qa2);
        Assert.Single(s2.Sandboxes.HoldoutRuns);
        Assert.Single(qa2.Asked);
    }
}
