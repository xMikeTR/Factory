using Factory.Core;
using Factory.Harness.Policy;
using Xunit;

namespace Factory.Integration.Tests.Policy;

public class CatalogFileTests
{
    private const string Full = """
        models:
          - id: vertex/claude-sonnet
            family: claude
            protocol: anthropicVertex
            modelName: claude-sonnet-5-5
            tier: standard
            caps: [toolUse, structuredOutput, vision]
            contextWindow: 1000000
            approvedFor: [public, internal, regulated]
            prior: [4, 1]
            price: { in: 3, out: 15 }
            source: placeholder
        """;

    [Fact]
    public void A_full_row_maps_every_field()
    {
        var r = CatalogFile.Parse(Full);

        Assert.Empty(r.Errors);
        var m = Assert.Single(r.Models);
        Assert.Equal("vertex/claude-sonnet", m.Id);
        Assert.Equal("claude", m.Family);
        Assert.True(m.Protocol.IsAnthropicVertex);
        Assert.Equal("claude-sonnet-5-5", m.ModelName);
        Assert.True(m.Tier.IsStandard);
        Assert.Equal(1_000_000, m.ContextWindow);
        Assert.Equal(3, m.Caps.Count);
        Assert.Equal(3, m.ApprovedFor.Count);
        Assert.Equal(4.0, m.Prior.Item1);
        Assert.Equal(1.0, m.Prior.Item2);
        Assert.Equal(3m, m.Price.InPerMTok);
        Assert.Equal(15m, m.Price.OutPerMTok);
    }

    [Fact]
    public void A_minimal_row_gets_defaults_so_adding_a_model_is_a_few_lines()
    {
        var r = CatalogFile.Parse("""
            models:
              - id: vertex/gemini-pro
                family: gemini
                modelName: gemini-2.5-pro
                tier: frontier
            """);

        Assert.Empty(r.Errors);
        var m = Assert.Single(r.Models);
        Assert.True(m.Protocol.IsVertexOpenAICompatible); // not a Claude model name
        Assert.Equal(200_000, m.ContextWindow);
        Assert.Equal(1.0, m.Prior.Item1);
        Assert.Equal(1.0, m.Prior.Item2);
        Assert.Contains(Capability.ToolUse, m.Caps);
        Assert.Contains(Capability.StructuredOutput, m.Caps);
        Assert.Contains(DataClass.Internal, m.ApprovedFor);
        Assert.DoesNotContain(DataClass.Regulated, m.ApprovedFor);
    }

    [Fact]
    public void A_claude_model_name_defaults_to_the_anthropic_path_and_the_claude_family()
    {
        var r = CatalogFile.Parse("models:\n  - id: c\n    modelName: claude-haiku-4-5\n    tier: small\n");

        Assert.Empty(r.Errors);
        var m = Assert.Single(r.Models);
        Assert.True(m.Protocol.IsAnthropicVertex);
        Assert.Equal("claude", m.Family);
    }

    [Fact]
    public void Bad_rows_are_reported_together_and_nothing_throws()
    {
        var r = CatalogFile.Parse("""
            models:
              - { id: a, family: x, modelName: m, tier: standard }
              - { id: a, family: x, modelName: m, tier: standard }
              - { id: b, family: x, modelName: m, tier: huge }
              - { id: c, family: x, modelName: m, tier: small, caps: [telepathy] }
              - { id: d, family: x, modelName: m, tier: small, protocol: carrierPigeon }
              - { id: e, family: x, modelName: m, tier: small, approvedFor: [secret] }
              - { id: f, family: x, modelName: m, tier: small, contextWindow: 0 }
              - { id: g, family: x, modelName: m, tier: small, price: { in: -1, out: 1 } }
              - { family: x, modelName: m, tier: small }
            """);

        Assert.Contains(r.Errors, e => e.Contains("duplicate") && e.Contains("a"));
        Assert.Contains(r.Errors, e => e.Contains("huge"));
        Assert.Contains(r.Errors, e => e.Contains("telepathy"));
        Assert.Contains(r.Errors, e => e.Contains("carrierPigeon"));
        Assert.Contains(r.Errors, e => e.Contains("secret"));
        Assert.Contains(r.Errors, e => e.Contains("contextWindow"));
        Assert.Contains(r.Errors, e => e.Contains("price"));
        Assert.Contains(r.Errors, e => e.Contains("id"));
    }

    [Fact]
    public void Invalid_yaml_and_an_empty_file_are_errors_not_exceptions()
    {
        Assert.NotEmpty(CatalogFile.Parse("models: [unclosed").Errors);
        Assert.NotEmpty(CatalogFile.Parse("").Errors);
    }

    [Fact]
    public void Load_uses_FACTORY_CATALOG_when_set_and_the_repo_default_otherwise()
    {
        var path = Path.Combine(Path.GetTempPath(), "s2-catalog-" + Guid.NewGuid().ToString("N") + ".yaml");
        File.WriteAllText(path, Full);

        var viaEnv = CatalogFile.Load(new Dictionary<string, string> { ["FACTORY_CATALOG"] = path });
        Assert.Empty(viaEnv.Errors);
        Assert.Single(viaEnv.Models);

        var missing = CatalogFile.Load(new Dictionary<string, string> { ["FACTORY_CATALOG"] = path + ".nope" });
        Assert.Contains(missing.Errors, e => e.Contains(".nope"));
    }

    [Fact]
    public void The_shipped_catalog_is_valid()
    {
        var shipped = CatalogFile.Load(new Dictionary<string, string> { ["FACTORY_CATALOG"] = CatalogFile.ShippedPath() });

        Assert.Empty(shipped.Errors);
        Assert.Contains(shipped.Models, m => m.Tier.IsSmall);
        Assert.Contains(shipped.Models, m => m.Tier.IsStandard);
        Assert.Contains(shipped.Models, m => m.Tier.IsFrontier);
    }
}
