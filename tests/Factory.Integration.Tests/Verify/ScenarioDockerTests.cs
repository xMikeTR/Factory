using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Git;
using Factory.S2.Verify;
using Xunit;

namespace Factory.Integration.Tests.Verify;

using Core = Factory.Core.Core;

/// F1 in the real runner image: designc's generated scenario test (Fixtures/scenarios, retargeted at the fixture's bordereaux screen) runs in
/// the ui: check. A page that sends the right body passes; one that sends a wrong field name fails and names the scenario and the example.
/// Opt-in (FACTORY_TEST_DOCKER=1, FACTORY_CONTAINER_ENGINE=docker).
[Trait("needs", "docker")]
public sealed class ScenarioDockerTests : IDisposable
{
    private static readonly bool Docker = Environment.GetEnvironmentVariable("FACTORY_TEST_DOCKER") == "1";
    private readonly string _state = Directory.CreateTempSubdirectory("scenario-docker").FullName;
    private int _n;

    private static string TestText() => File.ReadAllText(Path.Combine(UiPreviewFixture.FixtureRoot, "..", "scenarios", "orders.scenarios.page.test.tsx"))
        .Replace("../specs/orders.json", "../specs/bordereaux.json").Replace("{ OrdersList as Page } from \"../../src/pages/OrdersList\"", "{ BordereauxList as Page } from \"../../src/BordereauxList\"")
        .Replace("orders scenarios", "bordereaux scenarios");

    private async Task<(Handover H, GitWorkspace Git, string Integration, MemoryLedger Ledger)> SeedAsync(string page)
    {
        var fx = await UiPreviewFixture.CreateAsync();
        var state = Path.Combine(_state, "s" + ++_n);
        Directory.CreateDirectory(state);
        var h = UiHandoverDir.Create();
        var json = await HandoverInput.ResolveAsync(h.Dir, state, default);
        var handover = Core.ParseHandover(await File.ReadAllTextAsync(json)).WithScenarios([new ScenarioScreen("bordereaux", "BordereauxList",
            new UiText("ui-contract/tests/bordereaux.scenarios.page.test.tsx", "x", TestText()),
            Core.ToList([new ScenarioRef("ORD-1-ts-01", Tuple.Create("tagOrders", "POST", "/orders/tags"), Core.ToList([Tuple.Create(1, "a"), Tuple.Create(2, "b")]))]))]);
        var runDir = Path.Combine(state, "runs", "r1");
        HandoverInput.CopyUiPreviewsToRun(handover, json, runDir);
        var git = new GitWorkspace(fx.Repo, Path.Combine(state, "ws"));
        var integration = await git.EnsureIntegrationAsync("r1", "main", default);
        await UiContractSeed.SeedAsync(git, "r1", integration, handover, runDir, default);
        File.Copy(Path.Combine(UiPreviewFixture.FixtureRoot, page, "BordereauxList.tsx"), Path.Combine(integration, "src", "BordereauxList.tsx"), overwrite: true);
        return (handover, git, integration, new MemoryLedger());
    }

    private static PlannedTask Writer() =>
        new(TaskId.NewTaskId("T-1"), "title", "instructions", Core.ToList<ElementId>([]), Core.ToList([Plans.Pattern("src/BordereauxList.tsx")]), Core.ToList<PathPattern>([]),
            Core.ToList<TaskId>([]), Core.ToList<DecisionId>([]), Core.ToList<VerifyEntry>([]));

    private static string Tail = "";

    private static async Task<(List<JsonElement> Verifies, List<LedgerEvent> Failed)> RunAsync((Handover H, GitWorkspace Git, string Integration, MemoryLedger Ledger) s)
    {
        var outcomes = await new UiPageCheck(s.Ledger, new DockerSandboxFactory(new DockerSandboxOptions()), UiPageCheck.DefaultCommand)
            .RunAsync("r1", s.H, Core.ToList([Writer()]), s.Git, s.Integration, TimeSpan.FromMinutes(10), default);
        Tail = string.Join("\n", outcomes.Select(o => o.OutputTail));
        return ([.. s.Ledger.Events.Where(e => e.Kind == LedgerKinds.Verify).Select(e => JsonDocument.Parse(e.Payload!).RootElement.Clone())],
            [.. s.Ledger.Events.Where(e => e.Kind == LedgerKinds.UiCheckFailed && e.Payload!.Contains("\"scenario\""))]);
    }

    [Fact]
    public async Task A_page_sending_the_right_body_passes_and_a_wrong_field_name_fails_naming_the_scenario_and_example()
    {
        if (!Docker) return;
        var good = await RunAsync(await SeedAsync("ScenarioGood"));
        var gv = Assert.Single(good.Verifies, v => v.GetProperty("name").GetString() == "scenario:bordereaux");
        Assert.True(gv.GetProperty("ExitCode").GetInt32() == 0, gv.GetRawText() + Tail);
        Assert.Empty(good.Failed);

        var bad = await RunAsync(await SeedAsync("ScenarioWrongField"));
        var bv = Assert.Single(bad.Verifies, v => v.GetProperty("name").GetString() == "scenario:bordereaux");
        Assert.Equal(1, bv.GetProperty("ExitCode").GetInt32());
        var crit = bv.GetProperty("failures").EnumerateArray().Select(f => (f.GetProperty("criterion").GetString(), f.GetProperty("kind").GetString())).ToList();
        Assert.Equal([("ORD-1-ts-01 example 1", "body"), ("ORD-1-ts-01 example 2", "body")], crit);
        var row = JsonDocument.Parse(Assert.Single(bad.Failed.Take(1)).Payload!).RootElement;
        Assert.Equal("T-1", bad.Failed[0].TaskId);
        Assert.Equal("scenario", row.GetProperty("kind").GetString());
    }

    public void Dispose() => Directory.Delete(_state, recursive: true);
}
