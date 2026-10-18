using Factory.Core;
using Factory.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Factory.Integration.Tests.Agents;

public class ModelGatewaysTests
{
    private const string Key = "sk-test-key";

    private static ModelGateways From(bool dryRun, params (string Name, string Value)[] env) =>
        ModelGateways.FromEnvironment(env.ToDictionary(e => e.Name, e => e.Value), dryRun);

    private static readonly (string, string)[] Implementer = [("FACTORY_PROVIDER", "anthropic"), ("ANTHROPIC_API_KEY", Key)];

    [Fact]
    public void A_reviewer_on_another_family_is_independent()
    {
        var g = From(false, [.. Implementer,
            ("FACTORY_REVIEWER_PROVIDER", "openai"), ("FACTORY_REVIEWER_GATEWAY_KEY", "k2"), ("FACTORY_REVIEWER_MODEL_FAMILY", "gemini")]);

        Assert.Equal(Independence.Independent, g.ReviewerIndependence);
        Assert.Equal(ModelProvider.OpenAICompatible, g.Reviewer.Provider);
        Assert.Equal(ModelProvider.Anthropic, g.Implementer.Provider);
    }

    /// A reviewer on another family is likely not Claude: the implementer's cache_control is not passed on to it.
    [Fact]
    public void Cache_control_reaches_a_configured_reviewer_only_when_set_for_it()
    {
        (string, string)[] gateway = [("FACTORY_PROVIDER", "openai"), ("FACTORY_GATEWAY_KEY", Key), ("FACTORY_GATEWAY_CACHE_CONTROL", "on"),
            ("FACTORY_REVIEWER_GATEWAY_KEY", "k2"), ("FACTORY_MODEL_FAMILY", "claude"), ("FACTORY_REVIEWER_MODEL_FAMILY", "gemini")];

        Assert.Equal((true, false), (From(false, gateway).Implementer.CacheControl, From(false, gateway).Reviewer.CacheControl));
        Assert.True(From(false, [.. gateway, ("FACTORY_REVIEWER_GATEWAY_CACHE_CONTROL", "on")]).Reviewer.CacheControl);
    }

    [Fact]
    public void With_no_reviewer_configured_start_up_refuses_unless_same_family_is_allowed_on_dry_run_merges()
    {
        Assert.Throws<FactoryRejectedException>(() => From(true, Implementer));

        var allowed = From(true, [.. Implementer, ("FACTORY_REVIEWER_SAME_FAMILY", "allow")]);
        Assert.Equal(Independence.SameFamily, allowed.ReviewerIndependence);

        // the override is refused when merges are real
        Assert.Throws<FactoryRejectedException>(() => From(false, [.. Implementer, ("FACTORY_REVIEWER_SAME_FAMILY", "allow")]));
    }

    [Fact]
    public void An_unknown_family_on_either_side_counts_as_same_family()
    {
        // OpenAI-compatible gateway on both sides with no declared families: nothing proves independence.
        (string, string)[] gateway = [("FACTORY_PROVIDER", "openai"), ("FACTORY_GATEWAY_KEY", Key),
            ("FACTORY_REVIEWER_PROVIDER", "openai"), ("FACTORY_REVIEWER_GATEWAY_KEY", "k2")];
        Assert.Throws<FactoryRejectedException>(() => From(true, gateway));

        var declared = From(false, [.. gateway, ("FACTORY_MODEL_FAMILY", "claude"), ("FACTORY_REVIEWER_MODEL_FAMILY", "gemini")]);
        Assert.Equal(Independence.Independent, declared.ReviewerIndependence);

        Assert.Throws<FactoryRejectedException>(() =>
            From(false, [.. gateway, ("FACTORY_MODEL_FAMILY", "claude"), ("FACTORY_REVIEWER_MODEL_FAMILY", "Claude")]));
    }

    [Fact]
    public void Reviewer_and_QA_roles_use_the_reviewer_gateway_everything_else_the_implementers()
    {
        var g = From(true, [.. Implementer, ("FACTORY_REVIEWER_SAME_FAMILY", "allow"),
            ("FACTORY_REVIEWER_MODEL_STANDARD", "claude-reviewer-model")]);

        string Model(string role) => g.ChatClientFor(role, ModelTier.Standard).GetService<ChatClientMetadata>()!.DefaultModelId!;

        Assert.Equal("claude-reviewer-model", Model(Roles.Reviewer));
        Assert.Equal("claude-reviewer-model", Model(Roles.Qa));
        Assert.Equal("claude-sonnet-5-5", Model(Roles.Implementer));
        Assert.Equal("claude-sonnet-5-5", Model(Roles.TestAuthor));
    }
}
