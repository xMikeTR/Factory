using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// The reader's half of designc's `designc.handover.scenarios/1` (F1): scenarios.json becomes Handover.ScenarioTests, and a test
/// text whose sha256 differs from what scenarios.json names is refused.
public class ScenarioHandoverReaderTests
{
    private static string Fx(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", name);

    private static Task<Handover> Load(string dir) => new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None);

    /// designc's golden test text is the one whose sha256 the example names.
    public static string GoldenText() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "scenarios", "orders.scenarios.page.test.tsx"));

    /// A handover directory carrying only scenarios.json and its test file (edit the doc or the text with the arguments).
    public static string Dir(Action<JsonNode>? editDoc = null, string? testText = null, bool withKey = true, string fixture = "handover-scenarios.json")
    {
        var doc = JsonNode.Parse(File.ReadAllText(Fx(fixture)))!;
        editDoc?.Invoke(doc);
        var dir = Directory.CreateTempSubdirectory("scenario-handover").FullName;
        var files = new Dictionary<string, string>();
        void Put(string rel, string text) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, rel))!); File.WriteAllText(Path.Combine(dir, rel), text); files[rel] = text; }
        foreach (var s in doc["screens"]!.AsArray()) Put(s!["test"]!["path"]!.GetValue<string>(), testText ?? GoldenText());
        Put("scenarios.json", doc.ToJsonString());
        Put("elements.json", "{\"schema\":\"designc.handover.elements/1\",\"elements\":[]}");
        var yaml = "id: 20261120-scn\nelements: \"elements.json\"\n" + (withKey ? "scenario_tests: \"scenarios.json\"\n" : "") + "files:\n"
            + string.Concat(files.Select(f => $"  - {{ path: \"{f.Key}\", hash: \"{DesigncHandoverReader.Hash16(f.Value)}\" }}\n"));
        File.WriteAllText(Path.Combine(dir, "handover.yaml"), yaml);
        return dir;
    }

    [Fact]
    public void The_golden_text_is_the_one_the_contract_example_names()
    {
        var example = JsonNode.Parse(File.ReadAllText(Fx("handover-scenarios.json")))!;
        Assert.Equal(example["screens"]![0]!["test"]!["sha256"]!.GetValue<string>(), DesigncHandoverReader.Sha256(GoldenText()));
    }

    [Fact]
    public async Task The_contract_example_is_readable()
    {
        var h = await Load(Dir());
        var s = Assert.Single(h.ScenarioTests);
        Assert.Equal(("orders", "OrdersList", "ui-contract/tests/orders.scenarios.page.test.tsx"), (s.Screen, s.Component, s.Test.Path));
        Assert.Equal(GoldenText(), s.Test.Text);
        var c = Assert.Single(s.Scenarios);
        Assert.Equal("ORD-1-ts-01", c.Id);
        Assert.Equal(("tagOrders", "POST", "/orders/tags"), c.Operation.ToValueTuple());
        Assert.Equal([(1, "prefix match"), (2, "whole reference")], c.Examples.Select(e => e.ToValueTuple()));
        Assert.Equal(("ORD-1-ts-02", "minted: a person binds it before a test is generated"), Assert.Single(h.ScenarioSkipped).ToValueTuple());
    }

    [Fact]
    public async Task Without_the_key_there_are_no_scenario_tests()
    {
        var h = await Load(Dir(withKey: false));
        Assert.Empty(h.ScenarioTests);
        Assert.Empty(h.ScenarioSkipped);
    }

    [Fact]
    public async Task A_test_text_that_differs_from_the_sha256_scenarios_json_names_is_refused()
    {
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(Dir(testText: GoldenText() + "// edited\n")));
        Assert.Contains("orders.scenarios.page.test.tsx", e.Message);
        Assert.Contains("sha256", e.Message);
    }

    [Fact]
    public async Task Another_schema_is_refused()
    {
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(Dir(d => d["schema"] = "designc.handover.scenarios/9")));
        Assert.Contains("designc.handover.scenarios/1 or designc.handover.scenarios/2", e.Message);
    }

    [Fact]
    public async Task The_scenario_tests_survive_the_handover_json_round_trip()
    {
        var h = await Load(Dir());
        var back = Factory.Core.Core.ParseHandover(Factory.Core.Core.HandoverJson(h));
        Assert.Equal(h.ScenarioTests, back.ScenarioTests);
        Assert.Equal(h.ScenarioSkipped, back.ScenarioSkipped);
    }

    [Fact]
    public async Task The_scenarios_2_example_reads_its_api_rows_verbatim_and_lists_them_as_skipped_until_scenarios_3()
    {
        var h = await Load(Dir(fixture: "handover-scenarios-2.example.json"));
        Assert.Single(h.ScenarioTests);
        var a = Assert.Single(h.ApiScenarios);
        Assert.Equal(("ORD-1-ts-01", "tagOrders", "POST", "/orders/tags"), (a.Id, a.OperationId, a.Method, a.OpPath));
        Assert.Equal("test-scenarios/ORD-1-ts-01.yaml", a.Path);
        Assert.Equal([1, 2], a.Examples.Select(e => e.Index));
        var first = a.Examples[0];
        Assert.Equal("prefix match", first.Title);
        Assert.Equal("orders/Order", first.Given.Single().Item1);
        Assert.Equal(["{\"id\":1,\"ref\":\"A-100\"}", "{\"id\":2,\"ref\":\"B-200\"}"], first.Given.Single().Item2);
        Assert.Equal(["{\"id\":1,\"tag\":\"Priority\"}", "{\"id\":2,\"tag\":null}"], first.Then.Single().Item2);
        Assert.Empty(a.Examples[1].Given);
        Assert.All(a.Examples, e => Assert.True(Microsoft.FSharp.Core.OptionModule.IsNone(e.Request)));
        var ent = Assert.Single(h.Entities);
        Assert.Equal(("orders/Order", "orders", "Order", 1), (ent.Id, ent.Pack, ent.Name, ent.Version));
        Assert.Equal([("ref", "string"), ("tag", "string")], ent.Fields.Select(f => f.ToValueTuple()));
        Assert.Contains(("ORD-1-ts-01", DesigncHandoverReader.ApiNeedsRequest), h.ScenarioSkipped.Select(k => k.ToValueTuple()));
        Assert.Contains(("ORD-1-ts-02", "minted: a person binds it before a test is generated"), h.ScenarioSkipped.Select(k => k.ToValueTuple()));
    }

    [Fact]
    public async Task The_scenarios_1_fixture_has_no_api_scenarios_or_entities()
    {
        var h = await Load(Dir());
        Assert.Empty(h.ApiScenarios);
        Assert.Empty(h.Entities);
    }

    [Fact]
    public async Task The_api_scenarios_survive_the_handover_json_round_trip()
    {
        var h = await Load(Dir(fixture: "handover-scenarios-2.example.json"));
        var back = Factory.Core.Core.ParseHandover(Factory.Core.Core.HandoverJson(h));
        Assert.Equal(h.ApiScenarios, back.ApiScenarios);
        Assert.Equal(h.Entities, back.Entities);
    }
}
