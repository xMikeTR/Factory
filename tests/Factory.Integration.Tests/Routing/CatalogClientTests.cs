using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Microsoft.Extensions.AI;
using Xunit;

namespace Factory.Integration.Tests.Routing;

/// A catalog row becomes a client by its protocol, on the Vertex project and region from the environment. No call is made.
public class CatalogClientTests
{
    private static ModelGateways Vertex(params (string, string)[] extra) =>
        ModelGateways.FromEnvironment(new Dictionary<string, string>
        {
            ["FACTORY_PROVIDER"] = "vertex", ["FACTORY_VERTEX_PROJECT"] = "proj-1", ["FACTORY_VERTEX_REGION"] = "europe-west1",
            ["FACTORY_REVIEWER_MODEL_FAMILY"] = "gemini", ["FACTORY_MODEL_FAMILY"] = "claude",
        }.Concat(extra.ToDictionary(e => e.Item1, e => e.Item2)).ToDictionary(kv => kv.Key, kv => kv.Value), mergesAreDryRun: false);

    private static ModelEntry Row(string yamlRow) => CatalogFile.Parse($"models:\n  - {yamlRow}\n").Models.Single();

    [Fact]
    public void A_claude_row_gets_the_anthropic_vertex_client_for_its_model_name()
    {
        // The Anthropic Vertex client reads Application Default Credentials when built: with ADC it is built, without
        // it that is the failure. Either way the row took the Anthropic path (the OpenAI path never asks for ADC here).
        try
        {
            var client = Vertex().ChatClientFor(Row("{ id: c, modelName: claude-sonnet-5-5, tier: standard }"));
            Assert.Equal("claude-sonnet-5-5", client.GetService<ChatClientMetadata>()?.DefaultModelId);
        }
        catch (AggregateException e) when (e.Message.Contains("default credentials", StringComparison.OrdinalIgnoreCase)) { }
    }

    [Fact]
    public void A_gemini_row_gets_the_openai_compatible_vertex_client_for_its_model_name()
    {
        var client = Vertex().ChatClientFor(Row("{ id: g, family: gemini, modelName: some-gemini, tier: standard }"));

        Assert.Equal("some-gemini", client.GetService<ChatClientMetadata>()?.DefaultModelId);
    }

    [Fact]
    public void Catalog_routing_without_the_vertex_provider_is_refused_with_the_fix()
    {
        var gateways = ModelGateways.FromEnvironment(new Dictionary<string, string>
        {
            ["FACTORY_PROVIDER"] = "anthropic", ["ANTHROPIC_API_KEY"] = "k", ["FACTORY_REVIEWER_SAME_FAMILY"] = "allow",
        }, mergesAreDryRun: true);

        var e = Assert.Throws<FactoryRejectedException>(() => gateways.ChatClientFor(Row("{ id: c, modelName: claude-sonnet-5-5, tier: standard }")));

        Assert.Contains("FACTORY_PROVIDER=vertex", e.Message);
    }
}
