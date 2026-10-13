using Factory.Core;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// designc owns the handover's elements.json (its docs/handover-elements.schema.json); Fixtures/designc holds a
/// copy of its example. If this fails, designc changed the contract: conform here and refresh the copy.
public class DesigncHandoverElementsContractTests
{
    private static async Task<Handover> LoadExample(string fixture = "handover-elements.json")
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2-elements-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", fixture));
        await File.WriteAllTextAsync(Path.Combine(dir, "elements.json"), text);
        await File.WriteAllTextAsync(Path.Combine(dir, "handover.yaml"),
            $"id: 20261011-contract\nelements: \"elements.json\"\nfiles:\n  - {{ path: \"elements.json\", hash: {DesigncHandoverReader.Hash16(text)} }}\n");
        return await new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None);
    }

    [Fact]
    public async Task Code_backed_elements_become_the_handovers_elements()
    {
        var h = await LoadExample();
        Assert.Equal(
            [("Quotes/Quotes API", "Quotes API", RiskLevel.Medium, new[] { "src/Api/**", "src/Shared" }),
             ("Quotes/Quotes API/Pricing", "Pricing", RiskLevel.High, new[] { "src/Api/Pricing/**" })],
            h.Elements.Select(e => (e.Id.Value, e.Name, e.Risk, e.CodeAreas.Select(p => p.Value).ToArray())));
    }

    [Fact]
    public async Task Only_relationships_between_code_backed_elements_are_kept()
    {
        // The example's relationships both touch an element without code (the person, the DB).
        Assert.Empty((await LoadExample()).Relationships);
    }

    [Fact]
    public async Task Events_are_the_event_vocabulary()
    {
        Assert.Equal(["QuoteRequested", "QuoteSaved", "QuotePriced"], (await LoadExample()).EventVocabulary);
    }

    [Fact]
    public async Task Item_handover_reserved_paths_are_protected_paths()
    {
        var h = await LoadExample("handover-elements-2.json");
        Assert.Contains("tests/acceptance/**", h.ProtectedPaths.Select(p => p.Value));
    }
}
