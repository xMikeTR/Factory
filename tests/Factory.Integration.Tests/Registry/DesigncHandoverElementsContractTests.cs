using Factory.Core;
using Factory.Harness.Registry;
using Xunit;
using FactoryCore = global::Factory.Core.Core;

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

    [Fact]
    public async Task Item_handover_carries_out_of_scope_elements_as_context_and_design_handovers_are_all_in_scope()
    {
        var item = await LoadExample("handover-elements-2.json");
        Assert.Equal(
            [("Quotes/Quotes API", true), ("Quotes/Mail", false)],
            item.Elements.Select(e => (e.Id.Value, e.InScope)));
        Assert.All((await LoadExample()).Elements, e => Assert.True(e.InScope));
    }

    [Fact]
    public async Task Frozen_design_tokens_are_carried_as_text_and_a_handover_without_them_has_none()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2-tokens-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "design-tokens"));
        var elements = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "handover-elements.json"));
        const string tokens = "{ \"font\": { \"size\": { \"body\": { \"$value\": \"16px\", \"$type\": \"dimension\" } } } }";
        await File.WriteAllTextAsync(Path.Combine(dir, "elements.json"), elements);
        await File.WriteAllTextAsync(Path.Combine(dir, "design-tokens", "tokens.json"), tokens);
        await File.WriteAllTextAsync(Path.Combine(dir, "handover.yaml"),
            "id: 20261013-tokens\nelements: \"elements.json\"\ntokens: \"design-tokens/tokens.json\"\nfiles:\n" +
            $"  - {{ path: \"elements.json\", hash: {DesigncHandoverReader.Hash16(elements)} }}\n" +
            $"  - {{ path: \"design-tokens/tokens.json\", hash: {DesigncHandoverReader.Hash16(tokens)} }}\n");

        var h = await new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None);

        Assert.Equal(tokens, h.DesignTokens.Value);
        Assert.Equal(tokens, FactoryCore.ParseHandover(FactoryCore.HandoverJson(h)).DesignTokens.Value); // survives the run-dir copy
        Assert.True(Microsoft.FSharp.Core.FSharpOption<string>.get_IsNone((await LoadExample()).DesignTokens));
    }

    [Fact]
    public async Task Frozen_acceptance_specs_are_read_with_their_hash_and_a_handover_without_specs_has_none()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2-specs-" + Guid.NewGuid().ToString("N"));
        var specPath = "tests/acceptance/quotes-api/AC-1.spec.ts";
        Directory.CreateDirectory(Path.Combine(dir, "tests", "acceptance", "quotes-api"));
        var elements = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "handover-elements-2.json"));
        await File.WriteAllTextAsync(Path.Combine(dir, "elements.json"), elements);
        await File.WriteAllTextAsync(Path.Combine(dir, specPath), "// GENERATED\n");
        await File.WriteAllTextAsync(Path.Combine(dir, "handover.yaml"),
            "id: 20261013-specs\nelements: \"elements.json\"\n" +
            $"specs: [\"{specPath}\"]\nfiles:\n  - {{ path: \"elements.json\", hash: {DesigncHandoverReader.Hash16(elements)} }}\n" +
            $"  - {{ path: \"{specPath}\", hash: {DesigncHandoverReader.Hash16("// GENERATED\n")} }}\n");

        var h = await new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None);

        var spec = Assert.Single(h.AcceptanceSpecs);
        Assert.Equal((specPath, DesigncHandoverReader.Hash16("// GENERATED\n")), (spec.Path, spec.Hash));
        Assert.Empty((await LoadExample()).AcceptanceSpecs);
    }
}
