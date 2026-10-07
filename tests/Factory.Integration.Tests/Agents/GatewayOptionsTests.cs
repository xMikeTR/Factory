using Factory.Core;
using Factory.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Factory.Integration.Tests.Agents;

public class GatewayOptionsTests
{
    private const string Key = "sk-canary-0123456789";

    private static GatewayOptions From(params (string Name, string Value)[] env) =>
        GatewayOptions.FromEnvironment(env.ToDictionary(e => e.Name, e => e.Value));

    [Fact]
    public void Without_FACTORY_PROVIDER_the_gateway_is_OpenAI_compatible_with_LiteLLM_aliases()
    {
        var g = From(("FACTORY_GATEWAY_KEY", Key));

        Assert.Equal(ModelProvider.OpenAICompatible, g.Provider);
        Assert.Equal(new Uri("http://localhost:4000/v1"), g.Endpoint);
        Assert.Equal(("frontier", "standard", "small"), (g.FrontierModel, g.StandardModel, g.SmallModel));
    }

    [Fact]
    public void Anthropic_defaults_to_the_public_endpoint_and_Claude_models_and_takes_ANTHROPIC_API_KEY()
    {
        var g = From(("FACTORY_PROVIDER", "anthropic"), ("ANTHROPIC_API_KEY", Key), ("FACTORY_MODEL_SMALL", "claude-haiku-4-5-20251001"));

        Assert.Equal(ModelProvider.Anthropic, g.Provider);
        Assert.Null(g.Endpoint);
        Assert.Equal(Key, g.ApiKey);
        Assert.Equal("claude-sonnet-5-5", g.ModelFor(ModelTier.Standard));
        Assert.Equal("claude-haiku-4-5-20251001", g.ModelFor(ModelTier.Small));
    }

    [Fact]
    public void An_unknown_provider_or_a_missing_key_is_rejected()
    {
        Assert.Throws<FactoryRejectedException>(() => From(("FACTORY_PROVIDER", "vertex"), ("FACTORY_GATEWAY_KEY", Key)));
        Assert.Throws<FactoryRejectedException>(() => From(("FACTORY_PROVIDER", "anthropic")));
    }

    [Theory]
    [InlineData(ModelProvider.Anthropic, "anthropic")]
    [InlineData(ModelProvider.OpenAICompatible, "openai")]
    public void Each_provider_builds_its_own_chat_client_on_the_tiers_model(ModelProvider provider, string providerName)
    {
        var g = new GatewayOptions(provider, null, Key, "f", "s", "m");

        var meta = g.ChatClientFor(ModelTier.Standard).GetService<ChatClientMetadata>();

        Assert.Equal(providerName, meta?.ProviderName);
        Assert.Equal("s", meta?.DefaultModelId);
    }

    [Fact]
    public void The_key_never_appears_in_the_options_text()
    {
        var g = new GatewayOptions(ModelProvider.Anthropic, null, Key, "f", "s", "m");

        Assert.DoesNotContain(Key, g.ToString());
        Assert.Contains("Anthropic", g.ToString());
    }
}
