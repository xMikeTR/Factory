using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Generation;

public sealed class CodegenPhaseTests
{
    private const string Path1 = "contracts/openapi/fare-quotes.yaml";
    private const string Stem = "generated/contracts/csharp/FareQuotes/FareQuotes";

    private static Task<TeamHarness> HarnessAsync(FakeCodeGenerator gen, string yaml = "openapi: 3.0.0\n") =>
        TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: gen,
            shapeHandover: b => b.WithContract(b.Elements.Head.Id.Value, Path1, yaml));

    [Fact]
    public async Task A_handover_without_interfaces_logs_none_and_runs_no_generator()
    {
        var gen = new FakeCodeGenerator();
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: gen);
        Assert.Null(await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        Assert.Equal("none", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
        Assert.Equal(0, gen.Calls);
    }

    [Fact]
    public async Task Generation_writes_files_and_a_manifest_and_logs_generated()
    {
        var gen = new FakeCodeGenerator();
        using var h = await HarnessAsync(gen);
        Assert.Null(await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        var row = Assert.Single(h.Rows(LedgerKinds.Codegen));
        Assert.Equal("generated", row.Outcome);
        using var doc = JsonDocument.Parse(row.Payload!);
        Assert.Equal("nswag", doc.RootElement.GetProperty("tool").GetString());
        Assert.Equal(Codegen_Version, doc.RootElement.GetProperty("version").GetString());
        Assert.Equal([$"{Stem}.Dtos.g.cs", $"{Stem}.Server.g.cs"], doc.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToArray());
    }

    private static string Codegen_Version => Factory.Core.Codegen.nswagVersion;

    [Fact]
    public async Task Two_runs_on_the_same_handover_give_identical_files_and_changed_text_changes_them()
    {
        async Task<string> RunAsync(string yaml)
        {
            using var h = await HarnessAsync(new FakeCodeGenerator(), yaml);
            await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
            return await File.ReadAllTextAsync(Path.Combine(CodegenPhase.WorkDir(h.Services.Settings.RunDir(TeamHarness.RunId)), CodegenPhase.ManifestFile));
        }
        var a = await RunAsync("openapi: 3.0.0\n");
        Assert.Equal(a, await RunAsync("openapi: 3.0.0\n"));
        Assert.NotEqual(a, await RunAsync("openapi: 3.0.1\n"));
    }

    [Fact]
    public async Task A_failing_generator_ends_the_run_codegen_failed_with_the_reason()
    {
        using var h = await HarnessAsync(new FakeCodeGenerator("boom"));
        var finished = await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        Assert.Equal("codegen_failed", finished?.Status);
        Assert.Contains("boom", finished!.Detail);
        Assert.Equal("failed", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
    }

    [Fact]
    public async Task A_resume_reuses_what_was_generated()
    {
        var gen = new FakeCodeGenerator();
        using var h = await HarnessAsync(gen);
        await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        Assert.Equal(1, gen.Calls);
        Assert.Equal(["generated", "reused"], h.Rows(LedgerKinds.Codegen).Select(e => e.Outcome).ToArray());
    }

    [Fact]
    public async Task Off_logs_off_and_generates_nothing()
    {
        var gen = new FakeCodeGenerator();
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: gen,
            shapeHandover: b => b.WithContract(b.Elements.Head.Id.Value, Path1, "x"), shapeSettings: s => s with { CodegenOn = false });
        Assert.Null(await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        Assert.Equal("off", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
        Assert.Equal(0, gen.Calls);
    }

    [Fact]
    public async Task A_policy_naming_a_repo_the_run_does_not_have_is_refused()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: new FakeCodeGenerator(),
            shapeHandover: b => b.WithContract(b.Elements.Head.Id.Value, Path1, "x"), shapeSettings: s => s with { CodegenCsharp = ["Api"] });
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        Assert.Contains("Api", string.Join("\n", e.Errors));
    }

    [Fact]
    public async Task Seeding_replaces_the_generated_folder_on_the_integration_branch_and_commits_it_once()
    {
        using var h = await HarnessAsync(new FakeCodeGenerator());
        await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        var stale = Path.Combine(integration, "generated", "contracts", "csharp", "Old.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
        File.WriteAllText(stale, "// old\n");

        await CodegenPhase.SeedAsync(h.Services, TeamHarness.RunId, default);
        await CodegenPhase.SeedAsync(h.Services, TeamHarness.RunId, default); // a resume does nothing

        Assert.True(File.Exists(Path.Combine(integration, $"{Stem}.Dtos.g.cs")));
        Assert.True(File.Exists(Path.Combine(integration, $"{Stem}.Server.g.cs")));
        Assert.False(File.Exists(stale));
        var log = (await h.GitIn(integration, "log", "--format=%s")).Output;
        Assert.Single(log.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => l.StartsWith("factory: seed generated contracts"));
        Assert.Empty(CodegenPhase.Drift(h.Services, TeamHarness.RunId));

        File.AppendAllText(Path.Combine(integration, $"{Stem}.Dtos.g.cs"), "// edit\n");
        File.WriteAllText(Path.Combine(integration, "generated", "contracts", "csharp", "Extra.cs"), "x");
        var drift = Assert.Single(CodegenPhase.Drift(h.Services, TeamHarness.RunId)).Drift;
        Assert.Equal([$"added: generated/contracts/csharp/Extra.cs", $"modified: {Stem}.Dtos.g.cs"], drift.ToArray());
    }

    [Fact]
    public void The_policy_file_reads_the_csharp_repo_list_and_refuses_duplicates()
    {
        var dir = Directory.CreateTempSubdirectory("cgpol-").FullName;
        try
        {
            Assert.Null(CodegenPolicyFile.Load(dir));
            Directory.CreateDirectory(Path.Combine(dir, ".factory"));
            var file = Path.Combine(dir, MergePolicyFile.RelativePath);
            File.WriteAllText(file, "codegen:\n  csharp: [Api, Worker]\n");
            Assert.Equal(["Api", "Worker"], CodegenPolicyFile.Load(dir)!.ToArray());
            File.WriteAllText(file, "codegen:\n  csharp: [Api, Api]\n");
            Assert.Throws<FactoryRejectedException>(() => CodegenPolicyFile.Load(dir));
            Assert.Single(CodegenPolicyFile.Unknown(["Api", "Zed"], new HashSet<string> { "Api" }));
            Assert.Single(CodegenPolicyFile.Unknown(["Api"], new HashSet<string>()));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_failed_generation_stops_the_workflow_before_the_planner_is_called()
    {
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), codegen: new FakeCodeGenerator("boom"),
            shapeHandover: b => b.WithContract(b.Elements.Head.Id.Value, Path1, "x"));
        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new Approver(), default);
        Assert.Equal("codegen_failed", result?.Status);
        Assert.Empty(h.Rows(LedgerKinds.Plan));
        Assert.Equal("failed", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
    }

    private sealed class Approver : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "t"));
    }
}
