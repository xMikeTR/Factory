using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

/// The holdout must exist only in the registry and in the scratch copy. A run whose holdout FAILS (the worst case: the runner echoes the
/// sample into its failure message) is read end to end for the marker planted in the sample.
public sealed class UiHoldoutLeakTests
{
    private const string Marker = "LEAKMARK-7f3a91c2";

    private sealed class ApprovingHuman : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "test-approver"));
    }

    private static IEnumerable<string> Files(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) : [];

    private static bool Holds(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    [Fact]
    public async Task A_marker_in_the_holdout_is_in_no_file_row_brief_or_commit_of_a_failed_run_and_the_scratch_copy_is_gone()
    {
        var sample = File.ReadAllText(Path.Combine(UiHandoverDir.Registry, "ui", "holdout", "bordereaux.json")).Replace("Baltic Specialty", "Baltic " + Marker);
        Assert.Contains(Marker, sample);
        var ui = UiHandoverDir.Create(holdoutSha: Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sample))));
        var model = new ScriptedChatClient();
        var s = await UiWorkflowSetup.CreateAsync(model, ui, holdoutText: sample);
        UiWorkflowSetup.Script(model, s.Handover);
        s.Sandboxes.HoldoutFailing.Add("ready");
        var svc = s.Harness.Services;

        var result = await WorkflowHost.RunAsync(S2Workflow.Build(svc), new StartRun(TeamHarness.RunId, s.HandoverJson), new ApprovingHuman(), default);

        Assert.Equal("integration_failed", result?.Status);
        var run = Assert.Single(s.Sandboxes.HoldoutRuns);
        Assert.True(run.Sample, "the runner did get the sample: that is what the leak would come from");
        Assert.False(Directory.Exists(run.Workdir), "the scratch copy is disposed");

        // Every file the Factory keeps: its state dir (run root with the integration and team worktrees, RunDir with handover.json, plan,
        // dispatch, ui-baseline.json and briefs, the handover copies), and the registry except the one place the holdout lives.
        var integration = svc.Git.IntegrationPath(TeamHarness.RunId);
        var runRoot = Path.GetDirectoryName(integration)!;
        Assert.True(Directory.Exists(runRoot) && Directory.Exists(svc.Settings.RunDir(TeamHarness.RunId)));
        var registryHoldout = Path.Combine(svc.Settings.RegistryPath, "ui", "holdout");
        var roots = new[] { svc.Settings.StateDir, svc.Settings.RegistryPath };
        var read = 0;
        foreach (var file in roots.SelectMany(Files).Where(f => !f.StartsWith(registryHoldout)).Distinct())
        {
            read++;
            Assert.False(Holds(File.ReadAllBytes(file), Marker), $"{file} holds the holdout marker");
        }
        Assert.True(read > 20, $"only {read} files were read");
        Assert.Contains(Files(svc.Settings.RunDir(TeamHarness.RunId)), f => f.EndsWith("handover.json") || f.EndsWith("ui-baseline.json"));
        Assert.Contains(Files(runRoot), f => f.EndsWith("BordereauxList.tsx"));
        Assert.True(Directory.Exists(Path.Combine(svc.Settings.RegistryPath, "ledger", "runs")) && Files(Path.Combine(svc.Settings.RegistryPath, "ledger", "runs")).Any(),
            "the registry run stream exists and was read");

        // The run ledger, row by row, as the JSON it would be written as; and the repair brief built from it.
        foreach (var e in s.Harness.Ledger.Events) Assert.DoesNotContain(Marker, JsonSerializer.Serialize(e));
        var brief = await Repair.FromFailedRunAsync(svc, TeamHarness.RunId, default);
        Assert.NotNull(brief);
        Assert.DoesNotContain(Marker, JsonSerializer.Serialize(brief));
        Assert.Contains("hidden check", JsonSerializer.Serialize(brief));
        Assert.DoesNotContain(Marker, result!.Detail);

        // Nothing in git either: not on any branch of the repo.
        var log = await s.Harness.GitIn(svc.Settings.RepoPath, "log", "--all", "-S" + Marker, "--oneline");
        Assert.True(string.IsNullOrWhiteSpace(log.Output), log.Output);
    }
}
