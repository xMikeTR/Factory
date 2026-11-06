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

    private static StatsKey Key(string model) => new(model, Role.Implementer, Microsoft.FSharp.Core.FSharpOption<string>.None, SizeBucket.S);

    private static Counts Counts(double ok, double bad) => new(ok, bad, 1000, 100, (int)(ok + bad));

    [Fact]
    public async Task Learned_stats_decide_between_models_of_one_tier()
    {
        var h = await HandoverAsync();
        var stats = Microsoft.FSharp.Collections.MapModule.OfSeq(new[]
        {
            Tuple.Create(Key("c-std"), Counts(200, 0)),
            Tuple.Create(Key("g-std"), Counts(0, 200)),
        });

        foreach (var run in new[] { "run-1", "run-2", "run-3", "run-4" })
        {
            var table = RunRouter.Route(run, h, [Plans.Task("T-001")], Catalog(LearnCatalog), Config with { CanaryShare = 0 }, stats: stats);
            Assert.Equal("c-std", table.Find("T-001", Roles.Implementer)![ModelTier.Standard].Id); // the fixture element is Medium risk: Standard is the lowest tier routed
        }
    }

    [Fact]
    public async Task A_canary_share_of_one_swaps_standard_for_frontier_and_the_row_says_so()
    {
        var h = await HandoverAsync();
        var table = RunRouter.Route("run-1", h, [Plans.Task("T-001")], Catalog(TwoFamilies), Config with { CanaryShare = 1.0 });

        Assert.All(table.Rows, r => Assert.True(r.Canary));
        foreach (var r in table.Rows) Assert.Equal(r.Ladder["Frontier"], r.Ladder["Standard"]);
        Assert.Equal(table.Find("T-001", Roles.Implementer)![ModelTier.Frontier].Id, table.Find("T-001", Roles.Implementer)![ModelTier.Standard].Id);
    }

    [Fact]
    public async Task A_canary_share_of_zero_never_swaps_and_the_default_share_is_seeded_so_a_rerun_agrees()
    {
        var h = await HandoverAsync();
        var none = RunRouter.Route("run-1", h, [Plans.Task("T-001")], Catalog(TwoFamilies), Config with { CanaryShare = 0 });
        Assert.All(none.Rows, r => Assert.False(r.Canary));

        PlannedTask[] many = [.. Enumerable.Range(1, 60).Select(i => Plans.Task($"T-{i:000}"))];
        var a = RunRouter.Route("run-9", h, many, Catalog(TwoFamilies), Config).Rows.Select(r => r.Canary).ToList();
        var b = RunRouter.Route("run-9", h, many, Catalog(TwoFamilies), Config).Rows.Select(r => r.Canary).ToList();
        Assert.Equal(a, b);
        var rate = a.Count(x => x) / (double)a.Count;
        Assert.InRange(rate, 0.0, 0.2); // 240 draws at 5%
    }

    // --- phase 4: the budget state reaches the router ---

    [Fact]
    public async Task A_downgrade_budget_caps_every_ladder_at_standard_when_the_risk_minimum_allows_it()
    {
        var h = await HandoverAsync();
        var table = RunRouter.Route("run-1", h, [Plans.Task("T-001")], Catalog(TwoFamilies), Config, budget: BudgetState.Downgrade);
        foreach (var row in table.Rows)
            Assert.DoesNotContain(row.Ladder.Values, id => id.EndsWith("-top"));      // no frontier model anywhere
    }

    [Fact]
    public async Task A_blocked_budget_refuses_the_run_naming_the_budget()
    {
        var h = await HandoverAsync();
        var e = Assert.Throws<FactoryRejectedException>(() =>
            RunRouter.Route("run-1", h, [Plans.Task("T-001")], Catalog(TwoFamilies), Config, budget: BudgetState.Blocked));
        Assert.Contains(e.Errors, r => r.Contains("budget exceeded"));
    }

    [Fact]
    public async Task A_soft_budget_routes_and_carries_the_warning_on_the_rows()
    {
        var h = await HandoverAsync();
        var table = RunRouter.Route("run-1", h, [Plans.Task("T-001")], Catalog(TwoFamilies), Config, budget: BudgetState.Soft);
        Assert.All(table.Rows, r => Assert.Contains("budget soft limit reached", r.Warnings));
    }
}
