using System.Text.Json;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class FeatureRunnerTests
{
    private static JourneyCall Call(params (string, string)[] ps) =>
        new("/reg", "export-orders", "export-all", "web", "http://127.0.0.1:5101", ps, "/out/evidence");

    [Fact]
    public void Arguments_are_designcs_flags_in_order_with_params_after_the_base_url_and_json_last()
    {
        var args = FeatureRunner.Arguments("/d/scripts/feature-run.js", Call(("id", "7"), ("tenant", "a=b")));

        Assert.Equal(["/d/scripts/feature-run.js", "--registry", "/reg", "--feature", "export-orders", "--journey", "export-all",
            "--adapter", "web", "--base-url", "http://127.0.0.1:5101", "--param", "id=7", "--param", "tenant=a=b",
            "--out", "/out/evidence", "--json"], args);
    }

    [Fact]
    public void Writes_are_never_allowed()
    {
        var args = FeatureRunner.Arguments("s.js", Call());

        Assert.DoesNotContain("--allow-writes", args);
        Assert.DoesNotContain("--param", args);
    }

    private const string Passed = """[{"feature":"f","outcome":"pass","journeys":[{"journey":"j","entry":"menu","outcome":"pass","failures":[]},{"journey":"j","entry":null,"outcome":"pass","failures":[]}]}]""";
    private const string Failed = """[{"feature":"f","outcome":"fail","journeys":[{"journey":"j","entry":"menu","outcome":"fail","failures":[{"step":2,"kind":"state","msg":"link Download is missing"}]}]}]""";

    [Theory]
    [InlineData(0, Passed)]
    [InlineData(1, Failed)]
    [InlineData(3, Passed)]
    public void Exit_0_1_and_3_with_designcs_json_are_runs(int exit, string json)
    {
        var (results, error) = FeatureRunner.Interpret(exit, json, "");

        Assert.Null(error);
        var f = Assert.Single(results!);
        Assert.Equal("f", f.Feature);
        Assert.NotEmpty(f.Journeys);
        Assert.All(f.Journeys, j => Assert.Equal("j", j.Journey));
    }

    [Fact]
    public void The_json_becomes_runs_with_entry_outcome_and_failures()
    {
        var (results, _) = FeatureRunner.Interpret(1, Failed, "");

        var run = Assert.Single(results![0].Journeys);
        Assert.Equal(("menu", "fail"), (run.Entry, run.Outcome));
        Assert.Equal(new RunFailure(2, "state", "link Download is missing"), Assert.Single(run.Failures));
        Assert.Null(FeatureRunner.Interpret(0, Passed, "").Results![0].Journeys[1].Entry);
    }

    [Theory]
    [InlineData(2, "[]", "designc said no")]
    [InlineData(127, "", "node: not found")]
    [InlineData(0, "not json", "")]
    [InlineData(0, "{}", "")]
    [InlineData(1, """[{"feature":"f"}]""", "")]
    [InlineData(0, """[{"feature":"f","outcome":"maybe","journeys":[]}]""", "")]
    public void Exit_2_and_output_that_is_not_the_expected_json_are_errors_carrying_stderr(int exit, string stdout, string stderr)
    {
        var (results, error) = FeatureRunner.Interpret(exit, stdout, stderr);

        Assert.Null(results);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains(stderr, error);
    }

    private static JourneyRun Run(string j, string outcome, params RunFailure[] fs) => new(j, "menu", outcome, fs);

    [Fact]
    public void The_outcome_is_fail_then_could_not_run_then_pass()
    {
        Assert.Equal("pass", FeatureRunner.OutcomeOf([Run("a", "pass"), Run("b", "pass")]));
        Assert.Equal("could_not_run", FeatureRunner.OutcomeOf([Run("a", "pass"), Run("b", "could_not_run")]));
        Assert.Equal("fail", FeatureRunner.OutcomeOf([Run("a", "could_not_run"), Run("b", "fail")]));
    }

    [Fact]
    public void An_effect_failure_turns_only_the_passed_runs_of_that_journey_into_fail_at_the_step_after_the_last()
    {
        var runs = new[] { Run("a", "pass"), Run("a", "could_not_run"), Run("b", "pass"),
            Run("a", "fail", new RunFailure(1, "step", "no button")) };

        var merged = FeatureRunner.ApplyEffect(runs, "a", new EffectResult("fail", "effect GET /x: status 500, expected 200"), 4);

        Assert.Equal(["fail", "could_not_run", "pass", "fail"], merged.Select(r => r.Outcome));
        Assert.Equal(new RunFailure(4, "effect", "effect GET /x: status 500, expected 200"), Assert.Single(merged[0].Failures));
        Assert.Empty(merged[1].Failures);
        Assert.Equal(new RunFailure(1, "step", "no button"), Assert.Single(merged[3].Failures));
        Assert.Equal("fail", FeatureRunner.OutcomeOf(merged));
    }

    [Fact]
    public void An_effect_that_could_not_run_turns_passed_runs_into_could_not_run_without_failures()
    {
        var merged = FeatureRunner.ApplyEffect([Run("a", "pass"), Run("a", "fail")], "a", new EffectResult("could_not_run", "needs --param id"), 3);

        Assert.Equal(["could_not_run", "fail"], merged.Select(r => r.Outcome));
        Assert.Empty(merged[0].Failures);
    }

    [Fact]
    public void An_effect_that_holds_changes_nothing()
    {
        var runs = new[] { Run("a", "pass") };
        Assert.Equal(runs, FeatureRunner.ApplyEffect(runs, "a", new EffectResult("pass", null), 3));
    }

    [Fact]
    public void Failures_are_capped_at_50_per_run()
    {
        var many = Enumerable.Range(1, 60).Select(i => new RunFailure(i, "state", $"m{i}")).ToArray();

        var row = JsonDocument.Parse(FeatureResultRow.Build("f", "abc", [Run("a", "fail", many)], null)).RootElement;

        Assert.Equal(50, row.GetProperty("journeys")[0].GetProperty("failures").GetArrayLength());
    }

    [Fact]
    public void Rows_validate_against_the_pinned_ledger_schema_and_match_the_examples_layout()
    {
        var pass = FeatureResultRow.Build("export-orders", "3f2a9c1d", [Run("a", "pass"), new JourneyRun("a", null, "pass", [])], "W-feat");
        var fail = FeatureResultRow.Build("export-orders", "3f2a9c1d", [Run("a", "fail", new RunFailure(2, "state", "x"))], null);
        var cnr = FeatureResultRow.Build("export-orders", "3f2a9c1d", [Run("a", "could_not_run", new RunFailure(0, "ready", "the page is not signed in"))], null);

        foreach (var row in new[] { pass, fail, cnr })
            Assert.Empty(DesigncSchema.Validate(JsonSerializer.Serialize(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(row)!.Append(new("by", JsonDocument.Parse("\"factory-s2\"").RootElement)).ToDictionary(k => k.Key, k => k.Value))));
        var p = JsonDocument.Parse(pass).RootElement;
        Assert.Equal(["kind", "feature", "commit", "outcome", "journeys", "item"], p.EnumerateObject().Select(x => x.Name));
        Assert.Equal(["journey", "entry", "outcome"], p.GetProperty("journeys")[0].EnumerateObject().Select(x => x.Name));
        Assert.Equal(JsonValueKind.Null, p.GetProperty("journeys")[1].GetProperty("entry").ValueKind);
        Assert.Equal("pass", p.GetProperty("outcome").GetString());
        Assert.Equal("could_not_run", JsonDocument.Parse(cnr).RootElement.GetProperty("outcome").GetString());
        Assert.Equal(["journey", "entry", "outcome", "failures"], JsonDocument.Parse(fail).RootElement.GetProperty("journeys")[0].EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    public void The_item_is_present_only_when_given_and_the_row_has_no_by()
    {
        var without = JsonDocument.Parse(FeatureResultRow.Build("f", "abc", [Run("a", "pass")], null)).RootElement;
        var with = JsonDocument.Parse(FeatureResultRow.Build("f", "abc", [Run("a", "pass")], "W-1")).RootElement;

        Assert.False(without.TryGetProperty("item", out _));
        Assert.Equal("W-1", with.GetProperty("item").GetString());
        Assert.False(with.TryGetProperty("by", out _));
    }

    [Fact]
    public void A_feature_and_journey_are_picked_from_the_registry_and_an_unknown_one_is_named()
    {
        FeatureFile F(string id, params string[] js) => new(id, $"features/{id}.yaml", js.Select(j => new FeatureJourney(j, 1, false, null)).ToList(), []);
        var features = new[] { F("a", "j1", "j2"), F("b", "j2") };

        Assert.Equal(["a", "b"], FeatureRunner.Pick(features, "all", null).Picked!.Select(p => p.Id));
        Assert.Equal(["b"], FeatureRunner.Pick(features, "b", null).Picked!.Select(p => p.Id));
        Assert.Equal(["a"], FeatureRunner.Pick(features, "all", "j1").Picked!.Select(p => p.Id));
        Assert.Contains("nope", FeatureRunner.Pick(features, "nope", null).Error);
        Assert.Contains("nope", FeatureRunner.Pick(features, "all", "nope").Error);
    }
}
