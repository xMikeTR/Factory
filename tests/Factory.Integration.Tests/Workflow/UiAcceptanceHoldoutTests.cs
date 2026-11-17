using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

/// Acceptance cases marked `holdout: true`: designc generates the spec (a stand-in script plays `designc-core holdout-spec` here), the
/// Factory checks its hash against the freeze, runs it in a scratch copy, and names only screen, criterion and state afterwards.
public sealed class UiAcceptanceHoldoutTests : IDisposable
{
    private const string CaseMarker = "LEAKCASE-91b2";
    private static readonly string SpecText = $"import {{ test }} from '@playwright/test';\ntest('{CaseMarker} the hidden case', async () => {{}});\n";
    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private readonly string _designc = Directory.CreateTempSubdirectory("fake-designc").FullName;

    private sealed class ApprovingHuman : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "test-approver"));
    }

    private string FakeDesignc()
    {
        Directory.CreateDirectory(Path.Combine(_designc, "scripts"));
        var js = "const a = process.argv, g = n => a[a.indexOf(n) + 1];\n" +
                 "if (a[2] !== 'holdout-spec' || g('--criterion') !== 'AC-UI-1' || g('--case') !== '4' || !a.includes('--json')) { console.error('bad args'); process.exit(2); }\n" +
                 "const spec = " + JsonSerializer.Serialize(SpecText) + ";\n" +
                 "console.log(JSON.stringify({ spec, sha256: require('crypto').createHash('sha256').update(spec).digest('hex'), state: 'ready' }));\n";
        File.WriteAllText(Path.Combine(_designc, "scripts", "designc-core.js"), js);
        return _designc;
    }

    private static JsonElement P(LedgerEvent e) => JsonDocument.Parse(e.Payload!).RootElement;

    private static async Task<(UiWorkflowSetup Setup, RunFinished? Result)> RunAsync(string? designc, string? sha = null, Action<UiWorkflowSetup>? arrange = null)
    {
        var ui = UiHandoverDir.Create(holdoutCases: [("AC-UI-1", 4, "ready", sha ?? Sha(SpecText))]);
        var model = new ScriptedChatClient();
        var s = await UiWorkflowSetup.CreateAsync(model, ui, designcDir: designc, acceptanceCriterion: true);
        UiWorkflowSetup.Script(model, s.Handover);
        arrange?.Invoke(s);
        var result = await WorkflowHost.RunAsync(S2Workflow.Build(s.Harness.Services), new StartRun(TeamHarness.RunId, s.HandoverJson), new ApprovingHuman(), default);
        if (result?.Status == "plan_rejected") throw new Xunit.Sdk.XunitException(result.Detail);
        return (s, result);
    }

    [Fact]
    public async Task A_passing_case_runs_as_the_one_generated_spec_in_a_scratch_copy_that_is_gone_afterwards()
    {
        var (s, result) = await RunAsync(FakeDesignc());

        Assert.True(result?.Status == "merge_blocked", result?.Status + ": " + result?.Detail);
        var run = Assert.Single(s.Sandboxes.AcceptanceHoldoutRuns);
        Assert.Equal(["tests/acceptance/bordereauxlist/AC-UI-1.holdout-4.spec.ts"], run.Files);
        Assert.Contains("'tests/acceptance/bordereauxlist/AC-UI-1.holdout-4.spec.ts'", run.Command);
        Assert.False(Directory.Exists(run.Workdir));
        Assert.False(File.Exists(Path.Combine(s.Harness.Services.Git.IntegrationPath(TeamHarness.RunId), run.Files[0])));
        Assert.Empty(s.Harness.Rows(LedgerKinds.HoldoutFailed));
        Assert.Equal("pass", Assert.Single(s.Harness.Rows(LedgerKinds.UiResult)).Outcome);
    }

    [Fact]
    public async Task A_failing_case_gives_screen_criterion_and_state_rows_for_the_blamed_task_and_none_of_its_text_anywhere()
    {
        var (s, result) = await RunAsync(FakeDesignc(), arrange: a => a.Sandboxes.AcceptanceHoldoutFailing.Add("AC-UI-1.holdout-4.spec.ts"));

        Assert.Equal("integration_failed", result?.Status);
        var row = Assert.Single(s.Harness.Rows(LedgerKinds.HoldoutFailed));
        Assert.Equal("T-001", row.TaskId);
        Assert.Equal("{\"screen\":\"bordereaux\",\"criterion\":\"AC-UI-1\",\"state\":\"ready\"}", row.Payload);
        Assert.Equal("fail", Assert.Single(s.Harness.Rows(LedgerKinds.UiResult)).Outcome);
        Assert.Contains("holdout_failed: bordereaux (acceptance AC-UI-1) in state ready", result!.Detail);

        // The runner echoed the generated spec into its failure output: it is in no row, in the detail, in the brief, or in any file the Factory keeps.
        foreach (var e in s.Harness.Ledger.Events) Assert.DoesNotContain(CaseMarker, JsonSerializer.Serialize(e));
        Assert.DoesNotContain(CaseMarker, result.Detail);
        var svc = s.Harness.Services;
        var brief = await Repair.FromFailedRunAsync(svc, TeamHarness.RunId, default);
        Assert.NotNull(brief);
        Assert.DoesNotContain(CaseMarker, JsonSerializer.Serialize(brief));
        Assert.Contains("(acceptance AC-UI-1)", JsonSerializer.Serialize(brief));
        var read = 0;
        foreach (var file in new[] { svc.Settings.StateDir, svc.Settings.RegistryPath }.SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)))
        {
            read++;
            Assert.False(File.ReadAllText(file).Contains(CaseMarker), $"{file} holds the acceptance holdout case");
        }
        Assert.True(read > 20, $"only {read} files were read");
        Assert.True(string.IsNullOrWhiteSpace((await s.Harness.GitIn(svc.Settings.RepoPath, "log", "--all", "-S" + CaseMarker, "--oneline")).Output));
    }

    [Fact]
    public async Task Without_designc_the_case_could_not_run_and_the_run_is_unrunnable()
    {
        var (s, result) = await RunAsync(designc: null);

        Assert.Equal("integration_unrunnable", result?.Status);
        Assert.Contains("FACTORY_DESIGNC_DIR", result!.Detail);
        Assert.Empty(s.Sandboxes.AcceptanceHoldoutRuns);
        Assert.Equal("could_not_run", Assert.Single(s.Harness.Rows(LedgerKinds.UiResult)).Outcome);
    }

    [Fact]
    public async Task A_case_whose_hash_differs_from_the_freeze_could_not_run()
    {
        var (s, result) = await RunAsync(FakeDesignc(), sha: new string('b', 64));

        Assert.Equal("integration_unrunnable", result?.Status);
        Assert.Contains("changed since the freeze", result!.Detail);
        Assert.Empty(s.Sandboxes.AcceptanceHoldoutRuns);
        Assert.Equal("could_not_run", Assert.Single(s.Harness.Rows(LedgerKinds.UiResult)).Outcome);
    }

    public void Dispose() => Directory.Delete(_designc, recursive: true);
}
