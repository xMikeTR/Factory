using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Git;
using Xunit;

namespace Factory.Integration.Tests.Verify;

using Core = Factory.Core.Core;

/// F1: the ui: check reads designc's scenario tests from the page run's report and records one `scenario` verdict per screen, with no Docker:
/// a stand-in runner image answers with a canned Vitest JSON report.
public sealed class ScenarioCheckTests : IDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("scenario-check").FullName;

    private sealed class Canned(string? report) : ISandboxFactory
    {
        public Task<ICommandSandbox> ForTeamAsync(string teamId, string worktree, CancellationToken ct) => Task.FromResult<ICommandSandbox>(new Box(report));
        private sealed class Box(string? report) : ICommandSandbox
        {
            public string Kind => "docker";
            public Task<ProcessResult> RunAsync(string command, string workdir, TimeSpan timeout, IReadOnlyDictionary<string, string> env, CancellationToken ct)
            {
                if (report is not null)
                {
                    Directory.CreateDirectory(Path.Combine(workdir, ".vitest", "json"));
                    File.WriteAllText(Path.Combine(workdir, ".vitest", "json", "output.json"), report);
                }
                return Task.FromResult(new ProcessResult(1, "output", TimeSpan.Zero, false));
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static string Test(string title, string status, string msg = "") =>
        JsonSerializer.Serialize(new { title, ancestorTitles = new[] { "bordereaux scenarios" }, status, failureMessages = msg.Length == 0 ? [] : new[] { msg } });

    private static string Report(params string[] tests) => "{\"testResults\":[{\"assertionResults\":[" + string.Join(",", tests) + "]}]}";

    private sealed record Seeded(GitWorkspace Git, string Integration, Handover Handover, MemoryLedger Ledger);

    private async Task<Seeded> SeedAsync()
    {
        var fx = await UiPreviewFixture.CreateAsync();
        var h = UiHandoverDir.Create();
        var json = await HandoverInput.ResolveAsync(h.Dir, _state, default);
        var handover = Core.ParseHandover(await File.ReadAllTextAsync(json)).WithScenarios([new ScenarioScreen("bordereaux", "BordereauxList",
            new UiText("ui-contract/tests/bordereaux.scenarios.page.test.tsx", "x", "// generated\n"),
            Core.ToList([new ScenarioRef("ORD-1-ts-01", Tuple.Create("tagOrders", "POST", "/orders/tags"), Core.ToList([Tuple.Create(1, "a"), Tuple.Create(2, "b")]))]))]);
        var runDir = Path.Combine(_state, "runs", "r1");
        HandoverInput.CopyUiPreviewsToRun(handover, json, runDir);
        var git = new GitWorkspace(fx.Repo, Path.Combine(_state, "ws"));
        var integration = await git.EnsureIntegrationAsync("r1", "main", default);
        await UiContractSeed.SeedAsync(git, "r1", integration, handover, runDir, default);
        return new(git, integration, handover, new MemoryLedger());
    }

    private static PlannedTask Writer(string id, string path) =>
        new(TaskId.NewTaskId(id), "title", "instructions", Core.ToList<ElementId>([]), Core.ToList([Plans.Pattern(path)]), Core.ToList<PathPattern>([]),
            Core.ToList<TaskId>([]), Core.ToList<DecisionId>([]), Core.ToList<VerifyEntry>([]));

    private static async Task<List<JsonElement>> RunAsync(Seeded s, string? report)
    {
        await new UiPageCheck(s.Ledger, new Canned(report), UiPageCheck.DefaultCommand)
            .RunAsync("r1", s.Handover, Core.ToList([Writer("T-1", "src/BordereauxList.tsx")]), s.Git, s.Integration, TimeSpan.FromMinutes(1), default);
        return [.. s.Ledger.Events.Select(e => e.Payload is null ? default : JsonDocument.Parse(e.Payload).RootElement.Clone())];
    }

    private static JsonElement ScenarioVerify(Seeded s) =>
        s.Ledger.Events.Where(e => e.Kind == LedgerKinds.Verify).Select(e => JsonDocument.Parse(e.Payload!).RootElement)
            .Single(p => p.GetProperty("name").GetString() == "scenario:bordereaux");

    [Fact]
    public async Task A_wrong_body_blames_the_page_writer_and_names_the_scenario_and_example()
    {
        var s = await SeedAsync();
        await RunAsync(s, Report(Test("ORD-1-ts-01 example 1", "failed", "AssertionError: ORD-1-ts-01 example 1: POST /orders/tags body: expected { tagg: 'x' } to deeply equal { tag: 'x' }"),
            Test("ORD-1-ts-01 example 2", "passed")));
        var row = Assert.Single(s.Ledger.Events, e => e.Kind == LedgerKinds.UiCheckFailed);
        Assert.Equal("T-1", row.TaskId);
        var p = JsonDocument.Parse(row.Payload!).RootElement;
        Assert.Equal(("scenario", "ORD-1-ts-01 example 1", "body"), (p.GetProperty("kind").GetString(), p.GetProperty("criterion").GetString(), p.GetProperty("scenarioKind").GetString()));
        var v = ScenarioVerify(s);
        Assert.Equal(1, v.GetProperty("ExitCode").GetInt32());
        Assert.Equal("ORD-1-ts-01 example 1", v.GetProperty("failures")[0].GetProperty("criterion").GetString());
        Assert.Equal("fail", JsonDocument.Parse(Assert.Single(s.Ledger.Events, e => e.Kind == LedgerKinds.UiResult).Payload!).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Passing_scenario_tests_are_a_pass_for_the_scenario_stage()
    {
        var s = await SeedAsync();
        await RunAsync(s, Report(Test("ORD-1-ts-01 example 1", "passed"), Test("ORD-1-ts-01 example 2", "passed")));
        var v = ScenarioVerify(s);
        Assert.Equal(0, v.GetProperty("ExitCode").GetInt32());
        Assert.DoesNotContain(s.Ledger.Events, e => e.Kind == LedgerKinds.UiCheckFailed && e.Payload!.Contains("scenario"));
    }

    [Fact]
    public async Task Fewer_scenario_tests_than_examples_is_could_not_run_never_a_pass()
    {
        var some = await SeedAsync();
        await RunAsync(some, Report(Test("ORD-1-ts-01 example 1", "passed")));
        Assert.Equal(3, ScenarioVerify(some).GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public async Task No_report_is_could_not_run_never_a_pass()
    {
        var none = await SeedAsync();
        await RunAsync(none, null);
        Assert.Equal(3, ScenarioVerify(none).GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public async Task A_screen_with_no_scenario_tests_logs_no_scenario_verify()
    {
        var s = await SeedAsync();
        s = s with { Handover = s.Handover.WithScenarios([]) };
        await RunAsync(s, Report());
        Assert.DoesNotContain(s.Ledger.Events, e => e.Kind == LedgerKinds.Verify && e.Payload!.Contains("scenario:"));
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);
}
