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

    [Fact]
    public async Task Design_version_acceptance_criteria_and_loop_specs_are_carried_and_a_loop_spec_must_be_frozen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2-loop-" + Guid.NewGuid().ToString("N"));
        var spec = "tests/acceptance/quotes-api/AC-1.spec.ts";
        Directory.CreateDirectory(Path.Combine(dir, "tests", "acceptance", "quotes-api"));
        Directory.CreateDirectory(Path.Combine(dir, "acceptance"));
        var elements = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "handover-elements-2.json"));
        const string criterion = "id: AC-1\nrequirement: HRE-1\nelement: \"Quotes/Quotes API\"\nstates: [ready]\n";
        await File.WriteAllTextAsync(Path.Combine(dir, "elements.json"), elements);
        await File.WriteAllTextAsync(Path.Combine(dir, "acceptance", "AC-1.yaml"), criterion);
        await File.WriteAllTextAsync(Path.Combine(dir, spec), "// GENERATED\n");
        string Manifest(string loop) =>
            "id: 20261013-loop\nelements: \"elements.json\"\ndesign_version: r-abc123\nacceptance: [\"AC-1\"]\n" +
            $"specs: [\"{spec}\"]\nloop_specs: [{loop}]\nfiles:\n  - {{ path: \"elements.json\", hash: {DesigncHandoverReader.Hash16(elements)} }}\n" +
            $"  - {{ path: \"acceptance/AC-1.yaml\", hash: {DesigncHandoverReader.Hash16(criterion)} }}\n" +
            $"  - {{ path: \"{spec}\", hash: {DesigncHandoverReader.Hash16("// GENERATED\n")} }}\n";
        await File.WriteAllTextAsync(Path.Combine(dir, "handover.yaml"), Manifest($"\"{spec}\""));

        var h = await new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None);

        Assert.Equal("r-abc123", h.DesignVersion.Value);
        var c = Assert.Single(h.AcceptanceCriteria);
        Assert.Equal(("AC-1", "Quotes/Quotes API", criterion), (c.Id, c.Element, c.Text));
        Assert.Equal([spec], h.LoopSpecs);
        var back = FactoryCore.ParseHandover(FactoryCore.HandoverJson(h));
        Assert.Equal("r-abc123", back.DesignVersion.Value);
        Assert.Equal([spec], back.LoopSpecs);
        Assert.Equal("AC-1", back.AcceptanceCriteria.Single().Id);

        await File.WriteAllTextAsync(Path.Combine(dir, "handover.yaml"), Manifest("\"tests/acceptance/other.spec.ts\""));
        await Assert.ThrowsAnyAsync<Exception>(() => new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None));
    }

    [Fact]
    public async Task References_are_carried_as_key_and_kind_and_a_malformed_one_is_refused()
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2-refs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var elements = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "handover-elements-2.json"));
        await File.WriteAllTextAsync(Path.Combine(dir, "elements.json"), elements);
        string Manifest(string refs) =>
            $"id: 20261019-refs\nelements: \"elements.json\"\nreferences: {refs}\nfiles:\n  - {{ path: \"elements.json\", hash: {DesigncHandoverReader.Hash16(elements)} }}\n";
        await File.WriteAllTextAsync(Path.Combine(dir, "handover.yaml"), Manifest("[{ key: wpf, kind: legacy }]"));

        var h = await new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None);

        var r = Assert.Single(h.References);
        Assert.Equal(("wpf", "legacy"), (r.Key, r.Kind));
        Assert.Equal("wpf", FactoryCore.ParseHandover(FactoryCore.HandoverJson(h)).References.Single().Key);
        Assert.Empty((await LoadExample()).References);

        await File.WriteAllTextAsync(Path.Combine(dir, "handover.yaml"), Manifest("[{ kind: legacy }]"));
        await Assert.ThrowsAnyAsync<Exception>(() => new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None));
    }

    private static async Task<Handover> LoadWithContracts(string contractsYaml, string agentsJson, string? policyText = "name: quote-bot\n")
    {
        var dir = Path.Combine(Path.GetTempPath(), "s2-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "contracts"));
        var elements = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "designc", "handover-elements-2.json"));
        await File.WriteAllTextAsync(Path.Combine(dir, "elements.json"), elements);
        await File.WriteAllTextAsync(Path.Combine(dir, "agents.json"), agentsJson);
        var files = $"  - {{ path: \"elements.json\", hash: {DesigncHandoverReader.Hash16(elements)} }}\n  - {{ path: \"agents.json\", hash: {DesigncHandoverReader.Hash16(agentsJson)} }}\n";
        if (policyText is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "contracts", "quote-bot.yaml"), policyText);
            files += $"  - {{ path: \"contracts/quote-bot.yaml\", hash: {DesigncHandoverReader.Hash16(policyText)} }}\n";
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "handover.yaml"),
            $"id: 20261019-contracts\nelements: \"elements.json\"\nagents: \"agents.json\"\n{contractsYaml}files:\n{files}");
        return await new DesigncHandoverReader().LoadAsync(dir, S2Config.Default, CancellationToken.None);
    }

    private const string TwoAgents = "{\"schema\":\"designc.handover.agents/1\",\"agents\":[{\"id\":\"Q/Quote Bot\",\"name\":\"Quote Bot\",\"autonomy\":\"approve-writes\",\"tools\":[]},{\"id\":\"Q/Plain\",\"name\":\"Plain\",\"autonomy\":\"suggest\",\"tools\":[]}]}";

    [Fact]
    public async Task Contracts_are_read_and_a_suggest_agent_needs_none()
    {
        var h = await LoadWithContracts("contracts: {\"result\":\"contracts/result.json\",\"agents\":{\"Q/Quote Bot\":\"contracts/quote-bot.yaml\"},\"uncontracted\":[]}\n", TwoAgents);
        var c = Assert.Single(h.Contracts);
        Assert.Equal(("Q/Quote Bot", "contracts/quote-bot.yaml"), (c.Agent, c.Policy));
        Assert.Empty(h.Uncovered);
        Assert.Equal("Q/Quote Bot", FactoryCore.ParseHandover(FactoryCore.HandoverJson(h)).Contracts.Single().Agent); // survives the run-dir copy
    }

    [Fact]
    public async Task An_agent_with_no_contract_is_uncovered_unless_listed_uncontracted_and_old_handovers_load()
    {
        var none = await LoadWithContracts("", TwoAgents, null);
        Assert.Equal(["Q/Quote Bot"], none.Uncovered);
        Assert.Empty(none.Contracts);
        var listed = await LoadWithContracts("contracts: {\"uncontracted\":[\"Q/Quote Bot\"],\"agents\":{}}\n", TwoAgents, null);
        Assert.Empty(listed.Uncovered);
        Assert.Empty((await LoadExample()).Uncovered); // no agents.json: nothing to judge
    }

    [Fact]
    public async Task A_policy_that_is_not_a_frozen_file_is_refused()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => LoadWithContracts("contracts: {\"agents\":{\"Q/Quote Bot\":\"contracts/other.yaml\"}}\n", TwoAgents));
    }

    [Theory]
    [InlineData("off", false, false)]
    [InlineData("warn", false, true)]
    [InlineData(null, false, true)]
    [InlineData("require", true, false)]
    public async Task FACTORY_CONTRACTS_decides_between_silence_a_warning_and_a_refusal(string? mode, bool refuses, bool warns)
    {
        var h = await LoadWithContracts("", TwoAgents, null);
        var w = new StringWriter();
        if (refuses) Assert.Throws<FactoryRejectedException>(() => Factory.S2.CliOptions.RequireContracts(h, mode, w));
        else Factory.S2.CliOptions.RequireContracts(h, mode, w);
        Assert.Equal(warns, w.ToString().Contains("Q/Quote Bot"));
    }

    [Fact]
    public async Task Require_passes_when_every_agent_is_covered()
    {
        var h = await LoadWithContracts("contracts: {\"agents\":{\"Q/Quote Bot\":\"contracts/quote-bot.yaml\"}}\n", TwoAgents);
        Factory.S2.CliOptions.RequireContracts(h, "require", new StringWriter());
    }
}
