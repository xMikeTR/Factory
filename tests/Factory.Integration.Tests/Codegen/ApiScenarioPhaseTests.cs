using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.Integration.Tests.Workflow;
using Factory.S2;
using Factory.S2.Teams;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Generation;

/// X4 slice 3: the generated API tests are written by the codegen phase, seeded under generated/contracts/api-tests, drift-checked like the
/// rest of generated/contracts, and named in the briefs.
public sealed class ApiScenarioPhaseTests
{
    private const string Tests = "generated/contracts/api-tests";
    private const string Class = $"{Tests}/Orders/Ord1Ts01.ApiScenario.g.cs";
    private const string Runtime = $"{Tests}/ScenarioRuntime.g.cs";

    private sealed class Approver : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "t"));
    }

    private static Task<TeamHarness> HarnessAsync(bool withRequests = true, Func<FactorySettings, FactorySettings>? settings = null, ScriptedChatClient? model = null) =>
        TeamHarness.CreateAsync(model ?? new ScriptedChatClient(), new Limits(8, 3), codegen: new FakeCodeGenerator(),
            shapeHandover: b => ApiScenarioFixtures.Shape(b, withRequests), shapeSettings: settings,
            wrapSandboxes: inner => new FakeApiSandbox(inner),
            seedRepo: repo =>
            {
                Directory.CreateDirectory(Path.Combine(repo, "tests", "Orders.Tests"));
                File.WriteAllText(Path.Combine(repo, "tests", "Orders.Tests", "Orders.Tests.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"../../generated/contracts/api-tests/**/*.cs\" /></ItemGroup></Project>\n");
            });

    [Fact]
    public async Task The_phase_writes_the_runtime_and_a_class_and_lists_them_in_the_manifest_as_the_factorys_own()
    {
        using var h = await HarnessAsync();
        Assert.Null(await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        var work = CodegenPhase.WorkDir(h.Services.Settings.RunDir(TeamHarness.RunId));
        var repo = ".";
        Assert.True(File.Exists(Path.Combine(work, "out", repo, Class)));
        Assert.Equal(Factory.Core.ApiScenarios.runtimeText, File.ReadAllText(Path.Combine(work, "out", repo, Runtime)));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(work, CodegenPhase.ManifestFile)));
        var rows = doc.RootElement.GetProperty("files").EnumerateArray().Where(f => f.GetProperty("path").GetString()!.StartsWith(Tests)).ToList();
        Assert.Equal<IEnumerable<string?>>([Class, Runtime], rows.Select(f => f.GetProperty("path").GetString()));
        Assert.All(rows, f => Assert.Equal("factory", f.GetProperty("tool").GetString()));
        Assert.Equal("test-scenarios/ORD-1-ts-01.yaml", rows[0].GetProperty("source").GetString());
        Assert.Equal("0801fb767b1f662d9a80e2b2ab9e77cb1fe37333f1abcc1b360ea0f3a92e2f29", rows[0].GetProperty("source_sha256").GetString());
        // The serving repo also gets the generated client the tests call.
        Assert.Contains(doc.RootElement.GetProperty("files").EnumerateArray(), f => f.GetProperty("path").GetString()!.EndsWith("Orders.Client.g.cs"));
    }

    [Fact]
    public async Task Two_runs_give_byte_identical_manifests_and_files()
    {
        async Task<(string, string)> RunAsync()
        {
            using var h = await HarnessAsync();
            await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
            var work = CodegenPhase.WorkDir(h.Services.Settings.RunDir(TeamHarness.RunId));
            var repo = ".";
            return (File.ReadAllText(Path.Combine(work, CodegenPhase.ManifestFile)), File.ReadAllText(Path.Combine(work, "out", repo, Class)));
        }
        Assert.Equal(await RunAsync(), await RunAsync());
    }

    [Fact]
    public async Task Examples_without_a_request_generate_no_tests_and_no_client()
    {
        using var h = await HarnessAsync(withRequests: false);
        Assert.Null(await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        var work = CodegenPhase.WorkDir(h.Services.Settings.RunDir(TeamHarness.RunId));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(work, "out"), "*", SearchOption.AllDirectories), f => f.Contains("api-tests") || f.EndsWith("Client.g.cs"));
    }

    [Fact]
    public async Task Seeding_puts_the_tests_on_the_integration_branch_and_an_edit_is_drift()
    {
        using var h = await HarnessAsync();
        await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        await CodegenPhase.SeedAsync(h.Services, TeamHarness.RunId, default);
        var integration = h.Services.Git.IntegrationPath(TeamHarness.RunId);
        Assert.True(File.Exists(Path.Combine(integration, Class)));
        Assert.True(File.Exists(Path.Combine(integration, Runtime)));
        Assert.Empty(CodegenPhase.Drift(h.Services, TeamHarness.RunId));
        File.AppendAllText(Path.Combine(integration, Class), "// agent edit\n");
        Assert.Equal([$"modified: {Class}"], Assert.Single(CodegenPhase.Drift(h.Services, TeamHarness.RunId)).Drift.ToArray());
    }

    [Fact]
    public async Task With_codegen_off_the_scenarios_are_logged_skipped_once_and_nothing_is_generated()
    {
        using var h = await HarnessAsync(settings: s => s with { CodegenOn = false });
        Assert.Null(await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default));
        var row = Assert.Single(h.Rows(LedgerKinds.ApiScenariosSkipped));
        Assert.Contains("codegen off", row.Payload);
        Assert.Contains("ORD-1-ts-01", row.Payload);
        Assert.Equal("off", Assert.Single(h.Rows(LedgerKinds.Codegen)).Outcome);
    }

    [Fact]
    public async Task A_scenario_whose_operation_no_element_serves_fails_codegen_naming_the_scenario()
    {
        using var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), codegen: new FakeCodeGenerator(),
            shapeHandover: b => ApiScenarioFixtures.Shape(b).WithApiScenarios(ApiScenarioFixtures.Serving, ApiScenarioFixtures.Source, ApiScenarioFixtures.Yaml,
                [new ApiScenario("ORD-9", "p", "s", "noSuchOp", "GET", "/x", ApiScenarioFixtures.Scenario().Examples)]));
        var finished = await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        Assert.Equal("codegen_failed", finished?.Status);
        Assert.Contains("ORD-9", finished!.Detail);
    }

    [Fact]
    public async Task The_task_brief_names_the_api_tests_folder_the_adapter_and_the_read_only_rule()
    {
        using var h = await HarnessAsync();
        await CodegenPhase.GenerateAsync(h.Services, TeamHarness.RunId, h.Handover, default);
        var files = CodegenPhase.GeneratedFiles(h.Services.Settings.RunDir(TeamHarness.RunId));
        var task = Plans.Task("T-004");
        var serving = new PlannedTask(task.Id, task.Title, task.Instructions, Microsoft.FSharp.Collections.ListModule.OfSeq([ElementId.NewElementId(ApiScenarioFixtures.Serving)]), task.Writes, task.TestWrites, task.DependsOn, task.Covers, task.Verify);
        var text = Briefs.Task(h.Handover, serving, [], null, null, null, generated: files).Stable;
        Assert.Contains("## Generated API tests (read-only; the Factory wrote them)", text);
        Assert.Contains("ORD-1-ts-01", text);
        Assert.Contains("`generated/contracts/api-tests/`", text);
        Assert.Contains("`IScenarioAdapter`", text);
        Assert.Contains("The generated tests are read-only and must pass in integration verify.", text);
        Assert.DoesNotContain("Generated API tests", Briefs.Task(h.Handover, serving, [], null, null, null, generated: []).Text);
    }

    [Fact]
    public void The_planner_prompt_tells_the_serving_repos_first_task_to_wire_the_adapter()
    {
        var planner = Briefs.Prompt("planner").Replace("\n  ", " ");
        Assert.Contains("the first task of the serving repo creates or wires the test project and implements `IScenarioAdapter` once", planner);
        Assert.Contains("never `generated/`", planner);
    }

    private static async Task<(TeamHarness H, RunFinished? Result)> RunAsync(Action<string, TeamHarness>? tamper)
    {
        var probe = await HarnessAsync();
        var plan = S2WorkflowTests.PlanFor(probe.Handover);
        probe.Dispose();
        var model = new ScriptedChatClient();
        model.Enqueue(
            SubmitPlan(plan), Say("submitted"), Say("Approach: add BookingStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = "src/Fares.Data/BookingStatus.cs", content = "public enum BookingStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added BookingStatus" }), Say("done"));
        var h = await HarnessAsync(model: model);
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
    public async Task An_agent_edit_to_a_generated_test_fails_the_run_with_generated_code_modified_naming_the_file()
    {
        var (h, result) = await RunAsync((integration, _) => File.AppendAllText(Path.Combine(integration, Class), "// agent edit\n"));
        using var _ = h;
        Assert.Equal("integration_failed", result?.Status);
        Assert.Contains($"modified: {Class}", result!.Detail);
        Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Outcome == "generated_code_modified");
    }

    [Fact]
    public async Task Unchanged_seeded_tests_are_not_judged_by_the_merge()
    {
        var (h, result) = await RunAsync(null);
        using var _ = h;
        Assert.Equal("ready_for_review", result?.Status);
        Assert.DoesNotContain(h.Ledger.Events, e => (e.Payload ?? "").Contains("generated/contracts") && e.Kind == LedgerKinds.Merge);
        Assert.True(File.Exists(Path.Combine(h.Services.Git.IntegrationPath(TeamHarness.RunId), Class)));
    }
}
