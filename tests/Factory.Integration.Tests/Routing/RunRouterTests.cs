using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Harness.Registry;
using Factory.Harness.Routing;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Routing;

public class RunRouterTests
{
    private static readonly RoutingPolicyFile.RoutingConfig Config = RoutingPolicyFile.Load(Path.Combine(Path.GetTempPath(), "no-such-repo"));

    private static async Task<Handover> HandoverAsync() =>
        await new DesigncHandoverReader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "handover"), S2Config.Default, CancellationToken.None);

    private static IReadOnlyList<ModelEntry> Catalog(string yaml) => CatalogFile.Parse(yaml).Models;

    private const string TwoFamilies = """
        models:
          - { id: c-small, modelName: claude-haiku-4-5, tier: small }
          - { id: c-std, modelName: claude-sonnet-5-5, tier: standard }
          - { id: c-top, modelName: claude-opus-5-5, tier: frontier }
          - { id: g-small, family: gemini, modelName: g-flash, tier: small }
          - { id: g-std, family: gemini, modelName: g-mid, tier: standard }
          - { id: g-top, family: gemini, modelName: g-pro, tier: frontier }
        """;

    [Fact]
    public async Task Every_task_and_per_task_role_gets_a_full_ladder_that_a_rerun_reproduces()
    {
        var h = await HandoverAsync();
        PlannedTask[] tasks = [Plans.Task("T-001"), Plans.Task("T-002")];
        var catalog = Catalog(TwoFamilies);

        var a = RunRouter.Route("run-1", h, tasks, catalog, Config);
        var b = RunRouter.Route("run-1", h, tasks, catalog, Config);

        foreach (var t in tasks)
            foreach (var role in new[] { Roles.TaskPlanner, Roles.TestAuthor, Roles.Implementer, Roles.Reviewer })
            {
                var ladder = a.Find(t.Id.Value, role);
                Assert.NotNull(ladder);
                Assert.Equal(3, ladder!.Count);
                Assert.Equal(ladder[ModelTier.Standard].Id, b.Find(t.Id.Value, role)![ModelTier.Standard].Id);
            }
        Assert.Equal(8, a.Rows.Count);
        Assert.Null(a.Find("T-001", Roles.Planner)); // not routed per task: stays on the env model
        Assert.Null(a.Find("nope", Roles.Implementer));
    }

    [Fact]
    public async Task The_reviewer_never_shares_a_family_with_the_implementer_ladder()
    {
        var h = await HandoverAsync();
        var table = RunRouter.Route("run-1", h, [Plans.Task("T-001")], Catalog(TwoFamilies), Config);

        var impl = table.Find("T-001", Roles.Implementer)!.Select(kv => kv.Value.Family).ToHashSet();
        var review = table.Find("T-001", Roles.Reviewer)!.Select(kv => kv.Value.Family).ToHashSet();

        Assert.Empty(impl.Intersect(review));
    }

    [Fact]
    public async Task A_catalog_with_no_eligible_model_refuses_the_run_and_lists_the_reasons()
    {
        var h = await HandoverAsync();
        var onlySmallLowTrust = Catalog("models:\n  - { id: only, modelName: claude-haiku-4-5, tier: small, approvedFor: [public] }\n");

        var e = Assert.Throws<FactoryRejectedException>(() => RunRouter.Route("run-1", h, [Plans.Task("T-001")], onlySmallLowTrust, Config));

        Assert.Contains("only", e.Message);
        Assert.Contains("not approved for data class", e.Message);
    }

    [Fact]
    public async Task A_claude_only_catalog_cannot_give_the_reviewer_an_independent_family()
    {
        var h = await HandoverAsync();
        var claudeOnly = Catalog("""
            models:
              - { id: c-small, modelName: claude-haiku-4-5, tier: small }
              - { id: c-std, modelName: claude-sonnet-5-5, tier: standard }
              - { id: c-top, modelName: claude-opus-5-5, tier: frontier }
            """);

        var e = Assert.Throws<FactoryRejectedException>(() => RunRouter.Route("run-1", h, [Plans.Task("T-001")], claudeOnly, Config));

        Assert.Contains("family claude is excluded", e.Message);
    }

    [Fact]
    public async Task A_route_row_carries_role_size_risk_ladder_and_warnings()
    {
        var h = await HandoverAsync();
        var table = RunRouter.Route("run-1", h, [Plans.Task("T-001")], Catalog(TwoFamilies), Config);

        var row = table.Rows.Single(r => r.TaskId == "T-001" && r.Role == Roles.Implementer);

        Assert.Equal(["Small", "Standard", "Frontier"], row.Ladder.Keys.OrderBy(k => k == "Small" ? 0 : k == "Standard" ? 1 : 2));
        Assert.Contains(row.Ladder.Values, id => id.StartsWith("c-") || id.StartsWith("g-"));
        Assert.Contains(row.Size, new[] { "S", "M", "L" });
        Assert.Contains(row.Risk, new[] { "Low", "Medium", "High" });
        Assert.Empty(row.Warnings);
    }
}
