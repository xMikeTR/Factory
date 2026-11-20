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
    public static string Dir(Action<JsonNode>? editDoc = null, string? testText = null, bool withKey = true)
    {
        var doc = JsonNode.Parse(File.ReadAllText(Fx("handover-scenarios.json")))!;
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
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(Dir(d => d["schema"] = "designc.handover.scenarios/2")));
        Assert.Contains("designc.handover.scenarios/1", e.Message);
    }

    [Fact]
    public async Task The_scenario_tests_survive_the_handover_json_round_trip()
    {
        var h = await Load(Dir());
        var back = Factory.Core.Core.ParseHandover(Factory.Core.Core.HandoverJson(h));
        Assert.Equal(h.ScenarioTests, back.ScenarioTests);
        Assert.Equal(h.ScenarioSkipped, back.ScenarioSkipped);
    }
}
