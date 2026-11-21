using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Factory.Integration.Tests.Workflow;
using Factory.S2;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Verify;

/// X4 slice 4: integration verify runs the generated API tests in the serving repo and turns each failed example into a ledger row,
/// a stage verdict and a repair line that name it, with no dotnet or Docker.
public sealed class ApiScenarioCheckTests
{
    private const string Class = "generated/contracts/api-tests/Orders/Ord1Ts01.ApiScenario.g.cs";
    private const string Criterion = "ORD-1-ts-01 example 1";

    private sealed class Approver : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "t"));
    }

    private static async Task<(TeamHarness H, FakeApiSandbox Box, RunFinished? Result)> RunAsync(FakeApiSandbox.Mode mode, bool wired = true)
    {
        var probe = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), shapeHandover: b => ApiScenarioFixtures.Shape(b));
        var plan = S2WorkflowTests.PlanFor(probe.Handover);
        probe.Dispose();
        var model = new ScriptedChatClient();
        model.Enqueue(
            SubmitPlan(plan), Say("submitted"), Say("Approach: add BookingStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = "src/Fares.Data/BookingStatus.cs", content = "public enum BookingStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added BookingStatus" }), Say("done"));
        FakeApiSandbox? box = null;
        var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), codegen: new FakeCodeGenerator(), shapeHandover: b => ApiScenarioFixtures.Shape(b),
            wrapSandboxes: inner => box = new FakeApiSandbox(inner) { Answer = mode },
            seedRepo: repo =>
            {
                if (!wired) return;
                Directory.CreateDirectory(Path.Combine(repo, "tests", "Orders.Tests"));
                File.WriteAllText(Path.Combine(repo, "tests", "Orders.Tests", "Orders.Tests.csproj"),
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"../../generated/contracts/api-tests/**/*.cs\" /></ItemGroup></Project>\n");
            });
        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new Approver(), default);
        return (h, box!, result);
    }

    private static List<JsonElement> Verdicts(TeamHarness h) =>
        RegistryLedgerProjector.Project(h.Ledger.Events.ToList(), new HandoverRef("20261007-c4d072ba", [], "W-ui"), new(5, 3))
            .Where(e => e.Line.Contains("\"kind\":\"stage_verdict\"") && e.Line.Contains("\"stage\":\"scenario\"")).Select(e => JsonDocument.Parse(e.Line).RootElement.Clone()).ToList();

    [Fact]
    public async Task Right_tests_pass_and_the_run_is_ready_for_review()
    {
        var (h, box, result) = await RunAsync(FakeApiSandbox.Mode.Right);
        using var keep = h;
        Assert.Equal("ready_for_review", result?.Status);
        Assert.Single(box.Commands);
        Assert.Contains("dotnet test \"tests/Orders.Tests/Orders.Tests.csproj\"", box.Commands[0]);
        Assert.Contains("junit;LogFilePath=", box.Commands[0]);
        Assert.Empty(h.Rows(LedgerKinds.ApiScenarioFailed));
        Assert.Contains(h.Rows(LedgerKinds.Verify), e => e.Outcome == "passed" && e.Payload!.Contains("api-scenario:ORD-1-ts-01"));
        var v = Assert.Single(Verdicts(h));
        Assert.Equal(("pass", "ORD-1-ts-01"), (v.GetProperty("outcome").GetString(), v.GetProperty("scenario").GetString()));
        Assert.False(v.TryGetProperty("screen", out _));
    }

    [Fact]
    public async Task A_wrong_row_fails_the_run_and_the_message_ledger_verdict_and_repair_all_name_the_example()
    {
        var (h, _, result) = await RunAsync(FakeApiSandbox.Mode.WrongRows);
        using var keep = h;
        Assert.Equal("integration_failed", result?.Status);
        Assert.Contains(FakeApiSandbox.WrongMessage, result!.Detail);

        var row = Assert.Single(h.Rows(LedgerKinds.ApiScenarioFailed));
        Assert.Equal("failed", row.Outcome);
        Assert.NotNull(row.TaskId);
        using var p = JsonDocument.Parse(row.Payload!);
        Assert.Equal(("ORD-1-ts-01", 1, "prefix match", "rows"),
            (p.RootElement.GetProperty("scenario").GetString(), p.RootElement.GetProperty("example").GetInt32(), p.RootElement.GetProperty("title").GetString(), p.RootElement.GetProperty("kind").GetString()));
        Assert.Contains("orders/Order id=2: tag expected null, got \"Priority\"", p.RootElement.GetProperty("message").GetString());

        var verify = Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Payload!.Contains("api-scenario:ORD-1-ts-01"));
        Assert.Equal("failed", verify.Outcome);

        var v = Assert.Single(Verdicts(h));
        Assert.Equal(("fail", row.TaskId, "ORD-1-ts-01"), (v.GetProperty("outcome").GetString(), v.GetProperty("task").GetString(), v.GetProperty("scenario").GetString()));
        Assert.Equal(Criterion, v.GetProperty("failures")[0].GetProperty("criterion").GetString());
        Assert.Equal("rows", v.GetProperty("failures")[0].GetProperty("kind").GetString());

        var repair = await Repair.FromFailedRunAsync(h.Services, TeamHarness.RunId, default);
        Assert.Contains("API scenario ORD-1-ts-01 example 1 (prefix match) failed: ORD-1-ts-01 example 1 (prefix match): orders/Order id=2: tag expected null, got \"Priority\"",
            repair!.Feedback[row.TaskId!]);
    }

    [Fact]
    public async Task A_repo_with_no_test_project_compiling_the_folder_fails_with_the_wiring_message()
    {
        var (h, box, result) = await RunAsync(FakeApiSandbox.Mode.Right, wired: false);
        using var keep = h;
        Assert.Equal("integration_failed", result?.Status);
        Assert.Contains("no test project compiles generated/contracts/api-tests/**", result!.Detail);
        Assert.Empty(box.Commands);
        using var p = JsonDocument.Parse(Assert.Single(h.Rows(LedgerKinds.ApiScenarioFailed), e => e.Payload!.Contains("\"example\":1")).Payload!);
        Assert.Equal("seed", p.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_run_that_writes_no_results_could_not_run_which_is_not_a_pass_and_blames_no_task()
    {
        var (h, _, result) = await RunAsync(FakeApiSandbox.Mode.NoResults);
        using var keep = h;
        Assert.Equal("integration_unrunnable", result?.Status);
        Assert.Contains("api-scenarios", result!.Detail);
        Assert.Empty(h.Rows(LedgerKinds.ApiScenarioFailed));
        var verify = Assert.Single(h.Rows(LedgerKinds.Verify), e => e.Payload!.Contains("api-scenario:ORD-1-ts-01"));
        using var p = JsonDocument.Parse(verify.Payload!);
        Assert.Equal(3, p.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.Equal("could_not_run", Verdicts(h).Single().GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_tampered_generated_test_never_reaches_the_check()
    {
        var probe = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3), shapeHandover: b => ApiScenarioFixtures.Shape(b));
        var plan = S2WorkflowTests.PlanFor(probe.Handover);
        probe.Dispose();
        var model = new ScriptedChatClient();
        model.Enqueue(
            SubmitPlan(plan), Say("submitted"), Say("Approach: add BookingStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = "src/Fares.Data/BookingStatus.cs", content = "public enum BookingStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added BookingStatus" }), Say("done"));
        FakeApiSandbox? box = null;
        using var h2 = await TeamHarness.CreateAsync(model, new Limits(8, 3), codegen: new FakeCodeGenerator(), shapeHandover: b => ApiScenarioFixtures.Shape(b),
            wrapSandboxes: inner => box = new FakeApiSandbox(inner));
        var touched = false;
        h2.Ledger.OnAppend = e =>
        {
            if (touched || e.Outcome != "merged_to_integration") return;
            touched = true;
            File.AppendAllText(Path.Combine(h2.Services.Git.IntegrationPath(TeamHarness.RunId), Class), "// agent edit\n");
        };
        var result = await WorkflowHost.RunAsync(S2Workflow.Build(h2.Services), new StartRun(TeamHarness.RunId, h2.WriteHandoverJson()), new Approver(), default);
        Assert.Equal("integration_failed", result?.Status);
        Assert.Contains($"modified: {Class}", result!.Detail);
        Assert.Empty(box!.Commands);
    }
}
