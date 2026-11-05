using System.Text.Json;
using Factory.Core;
using Factory.Harness.Policy;
using Factory.Harness.Routing;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Xunit;

namespace Factory.Integration.Tests.Routing;

public class RouterStatsReportTests
{
    private static readonly IReadOnlyList<ModelEntry> Catalog = CatalogFile.Parse("""
        models:
          - { id: c-std, modelName: claude-sonnet-5-5, tier: standard, price: { in: 3, out: 15 } }
          - { id: c-top, modelName: claude-opus-5-5, tier: frontier, price: { in: 15, out: 75 } }
        """).Models;

    private static StatsKey Key(string model, Role role, SizeBucket size) => new(model, role, FSharpOption<string>.None, size);

    private static FSharpMap<StatsKey, Counts> Stats() => MapModule.OfSeq(new[]
    {
        Tuple.Create(Key("c-top", Role.Implementer, SizeBucket.L), new Counts(1, 0, 2000, 200, 1)),
        Tuple.Create(Key("c-std", Role.Implementer, SizeBucket.S), new Counts(9, 1, 1000, 100, 10)),
        Tuple.Create(Key("c-std", Role.Reviewer, SizeBucket.S), new Counts(0, 3, 500, 50, 3)),
    });

    [Fact]
    public void Cost_per_success_is_cost_per_attempt_at_current_prices_over_the_pass_rate()
    {
        var row = RouterStatsReport.Rows(Stats(), Catalog, role: null).Single(r => r.Model == "c-std" && r.Role == "Implementer");

        Assert.Equal(9.0, row.Successes);
        Assert.Equal(1.0, row.Failures);
        Assert.Equal(10, row.Samples);
        Assert.Equal(0.005m, row.CostPerSuccess!.Value, 6); // (1000*3 + 100*15) / 1e6 = 0.0045, over 0.9
    }

    [Fact]
    public void A_key_with_no_success_has_no_cost_per_success_and_rows_are_sorted()
    {
        var rows = RouterStatsReport.Rows(Stats(), Catalog, role: null);

        Assert.Null(rows.Single(r => r.Role == "Reviewer").CostPerSuccess);
        Assert.Equal(["c-std", "c-std", "c-top"], rows.Select(r => r.Model));
    }

    [Fact]
    public void The_role_filter_is_case_insensitive_and_rejects_nonsense()
    {
        Assert.Single(RouterStatsReport.Rows(Stats(), Catalog, role: "reviewer"));
        var e = Assert.Throws<ArgumentException>(() => RouterStatsReport.Rows(Stats(), Catalog, role: "wizard"));
        Assert.Contains("wizard", e.Message);
    }

    [Fact]
    public void Json_and_text_renderings_carry_the_same_rows()
    {
        var rows = RouterStatsReport.Rows(Stats(), Catalog, role: null);

        var json = JsonDocument.Parse(RouterStatsReport.Render(rows, json: true)).RootElement;
        Assert.Equal(3, json.GetArrayLength());
        Assert.Equal("c-std", json[0].GetProperty("model").GetString());
        Assert.Equal(0.005, json[0].GetProperty("cost_per_success").GetDecimal() is var d ? (double)d : 0, 6);

        var text = RouterStatsReport.Render(rows, json: false);
        Assert.Contains("model", text);
        Assert.Contains("c-top", text);
        Assert.Equal("no learned stats yet\n", RouterStatsReport.Render([], json: false));
    }
}
