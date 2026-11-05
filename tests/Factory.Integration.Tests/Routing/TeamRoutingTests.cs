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
}
