using Factory.Core;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// The reader's half of designc's `designc.handover.tests/1` (F5): tests.json becomes Handover.Tests, and any other schema is refused.
public class TestMapReaderTests
{
    private static Task<Handover> Load(string dir) => new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None);

    private static string Write(string testsJson)
    {
        var dir = Directory.CreateTempSubdirectory("tests-map").FullName;
        const string elements = "{\"schema\":\"designc.handover.elements/1\",\"elements\":[]}";
        File.WriteAllText(Path.Combine(dir, "elements.json"), elements);
        File.WriteAllText(Path.Combine(dir, "tests.json"), testsJson);
        File.WriteAllText(Path.Combine(dir, "handover.yaml"),
            "id: 20261117-tests\nelements: \"elements.json\"\ntests: \"tests.json\"\nfiles:\n"
            + $"  - {{ path: \"elements.json\", hash: \"{DesigncHandoverReader.Hash16(elements)}\" }}\n"
            + $"  - {{ path: \"tests.json\", hash: \"{DesigncHandoverReader.Hash16(testsJson)}\" }}\n");
        return dir;
    }

    [Fact]
    public async Task The_contract_example_is_readable()
    {
        var example = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "handover-tests.json"));
        var map = (await Load(Write(example))).Tests.Value;
        Assert.Equal(3, map.Elements.Length);
        var portal = map.Elements.Single(e => e.Element == "Fares/Fares Portal");
        Assert.Equal(["acceptance", "ui-page"], portal.FailToPass.Select(t => t.Kind));
        Assert.Equal("npx vitest run tests/portal/format.test.ts", portal.PassToPass.Single().Command.Value);
        Assert.Equal("import", portal.PassToPass.Single().Via.Value);
        var (element, reason) = map.Unmapped.Single();
        Assert.Equal("Fares/Surcharge Engine", element);
        Assert.Equal("no existing test references its code", reason);
    }

    [Fact]
    public async Task A_handover_without_a_tests_key_has_no_test_map()
    {
        var dir = Directory.CreateTempSubdirectory("tests-none").FullName;
        const string elements = "{\"schema\":\"designc.handover.elements/1\",\"elements\":[]}";
        File.WriteAllText(Path.Combine(dir, "elements.json"), elements);
        File.WriteAllText(Path.Combine(dir, "handover.yaml"),
            $"id: 20261117-none\nelements: \"elements.json\"\nfiles:\n  - {{ path: \"elements.json\", hash: \"{DesigncHandoverReader.Hash16(elements)}\" }}\n");
        Assert.Null((await Load(dir)).Tests);
    }

    [Fact]
    public async Task Another_schema_is_refused()
    {
        var e = await Assert.ThrowsAsync<FactoryRejectedException>(() => Load(Write("{\"schema\":\"designc.handover.tests/2\",\"elements\":[],\"unmapped\":[]}")));
        Assert.Contains("designc.handover.tests/1", e.Message);
    }
}
