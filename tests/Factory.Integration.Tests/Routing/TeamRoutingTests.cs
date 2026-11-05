using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Harness.Routing;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Teams;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Routing;

public class TeamRoutingTests
{
    private const string NewFile = "src/Pricing.Data/QuoteStatus.cs";
    private static readonly IReadOnlyDictionary<string, DecisionAnswer> NoAnswers = new Dictionary<string, DecisionAnswer>();
    private static PlannedTask T001() => Plans.Task("T-001", Plans.Auto("file exists", $"test -f {NewFile}"));

    private const string Catalog = """
        models:
          - { id: c-small, modelName: claude-haiku-4-5, tier: small }
          - { id: c-std, modelName: claude-sonnet-5-5, tier: standard }
          - { id: c-top, modelName: claude-opus-5-5, tier: frontier }
          - { id: g-small, family: gemini, modelName: g-flash, tier: small }
          - { id: g-std, family: gemini, modelName: g-mid, tier: standard }
          - { id: g-top, family: gemini, modelName: g-pro, tier: frontier }
        """;


    /// Three families, priced: stats only matter when scores differ. Mistral costs ten times as much, so it never wins
    /// a tier, but it is the family the reviewer is left with (the implementer ladder may span claude and gemini).
    private const string LearnCatalog = """
        models:
          - { id: c-small, modelName: claude-haiku-4-5, tier: small, price: { in: 1, out: 5 } }
          - { id: c-std, modelName: claude-sonnet-5-5, tier: standard, price: { in: 3, out: 15 } }
          - { id: c-top, modelName: claude-opus-5-5, tier: frontier, price: { in: 15, out: 75 } }
          - { id: g-std, family: gemini, modelName: g-mid, tier: standard, price: { in: 3, out: 15 } }
          - { id: m-small, family: mistral, modelName: m-s, tier: small, price: { in: 10, out: 50 } }
          - { id: m-std, family: mistral, modelName: m-m, tier: standard, price: { in: 30, out: 150 } }
          - { id: m-top, family: mistral, modelName: m-t, tier: frontier, price: { in: 150, out: 750 } }
        """;

    private static ScriptedChatClient Model() => new(
        Call("list_files", new { directory = "src/Pricing.Data" }), Say("Approach: add QuoteStatus.cs"),
        Call("declare_done", new { summary = "verify block already fails" }), Say("done"),
        Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
        Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));

    private static RoutingSetup Setup(string catalogYaml, ScriptedChatClient model) =>
        new(CatalogFile.Parse(catalogYaml).Models, RoutingPolicyFile.Load(Path.Combine(Path.GetTempPath(), "none")), _ => model);

    [Fact]
    public async Task With_routing_on_turn_rows_carry_the_catalog_model_id_and_a_route_row_is_written_per_pair()
    {
        var model = Model();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), routing: Setup(Catalog, model));
        await h.Services.RouteAsync(TeamHarness.RunId, h.Handover, [T001()], logRows: true, default);

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        var routes = h.Rows(LedgerKinds.Route).ToList();
        Assert.Equal(4, routes.Count);
        var p = JsonDocument.Parse(routes[0].Payload!).RootElement;
        Assert.Equal(3, p.GetProperty("ladder").EnumerateObject().Count());
        foreach (var f in new[] { "role", "size", "risk", "warnings" }) Assert.True(p.TryGetProperty(f, out _), f);
        Assert.Equal("T-001", routes[0].TaskId);

        var ids = h.Rows(LedgerKinds.Turn).Select(t => JsonDocument.Parse(t.Payload!).RootElement.GetProperty("model_id").GetString()).ToList();
        Assert.Equal(3, ids.Count);
        Assert.All(ids, id => Assert.Matches("^(c|g)-(small|std|top)$", id!));

        // the review verdict names the reviewer's model, so learning can credit it
        var reviews = h.Rows(LedgerKinds.Review).ToList();
        Assert.NotEmpty(reviews);
        Assert.All(reviews, r => Assert.Matches("^(c|g)-(small|std|top)$", JsonDocument.Parse(r.Payload!).RootElement.GetProperty("model_id").GetString()!));
    }

    [Fact]
    public async Task With_routing_off_there_are_no_route_rows_and_no_model_id()
    {
        var model = Model();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        await h.Services.RouteAsync(TeamHarness.RunId, h.Handover, [T001()], logRows: true, default); // a no-op

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(T001()), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        Assert.Empty(h.Rows(LedgerKinds.Route));
        Assert.All(h.Rows(LedgerKinds.Turn), t => Assert.False(JsonDocument.Parse(t.Payload!).RootElement.TryGetProperty("model_id", out _)));
    }

    [Fact]
    public async Task A_catalog_with_no_eligible_model_refuses_the_run_listing_the_reasons_and_writes_no_route_rows()
    {
        var model = Model();
        var onlyPublic = "models:\n  - { id: only, modelName: claude-haiku-4-5, tier: small, approvedFor: [public] }\n";
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), routing: Setup(onlyPublic, model));

        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => h.Services.RouteAsync(TeamHarness.RunId, h.Handover, [T001()], logRows: true, default).AsTask());

        Assert.Contains("only", e.Message);
        Assert.Contains("not approved for data class", e.Message);
        Assert.Empty(h.Rows(LedgerKinds.Route));
    }

    [Fact]
    public async Task Routing_at_run_start_reads_learned_stats_from_the_state_dir_ledger()
    {
        var model = Model();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3), routing: Setup(LearnCatalog, model) with
        {
            Config = RoutingPolicyFile.Load(Path.Combine(Path.GetTempPath(), "none")) with { CanaryShare = 0 },
        });
        // history: g-std passed 60 implementer verifies, c-std failed 60 (zero prices tie, so without history the first catalog row, c-std, wins)
        Directory.CreateDirectory(h.StateDir);
        var past = new JsonlLedger(Path.Combine(h.StateDir, "ledger.jsonl"));
        foreach (var (model_id, passed) in new[] { ("g-std", true), ("c-std", false) })
            for (var i = 0; i < 60; i++)
            {
                var run = $"old-{model_id}-{i}";
                await past.AppendAsync(new LedgerEvent(run, "S2", "route", LedgerKinds.Route, TaskId: "T-9", Payload: JsonSerializer.Serialize(new { role = Roles.Implementer, size = "S" })));
                await past.AppendAsync(new LedgerEvent(run, "S2", "act", LedgerKinds.Turn, TaskId: "T-9", TokensIn: 1000, TokensOut: 100, Payload: JsonSerializer.Serialize(new { role = Roles.Implementer, model_id })));
                await past.AppendAsync(new LedgerEvent(run, "S2", "task", LedgerKinds.TaskEvent, TaskId: "T-9", Outcome: passed ? "Reviewing" : "Ready", Payload: JsonSerializer.Serialize(new { type = "verify_completed" })));
            }

        // every run id must follow the history
        foreach (var run in Enumerable.Range(1, 8).Select(i => $"run-{i}"))
        {
            await h.Services.RouteAsync(run, h.Handover, [T001()], logRows: false, default);
            Assert.Equal("g-std", h.Services.Agents.ModelIdFor(Roles.Implementer, ModelTier.Standard, "T-001"));
        }
    }
}
