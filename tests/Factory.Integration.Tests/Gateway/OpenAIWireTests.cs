using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Microsoft.Extensions.AI;
using Xunit;

namespace Factory.Integration.Tests.Gateway;

/// What S2 sends to an OpenAI-compatible gateway, against a local fake of it.
public class OpenAIWireTests
{
    private const string Reply = """
        {"id":"c1","object":"chat.completion","created":1,"model":"standard",
         "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":10,"completion_tokens":1,"total_tokens":11}}
        """;

    private static readonly AgentScope Scope = new("run-1", Roles.TaskPlanner, "team-T-001", "T-001");

    /// OpenAI routes requests with the same prompt_cache_key to the same cache; one agent of one task keeps one key.
    [Fact]
    public async Task An_agent_sends_a_prompt_cache_key_naming_its_run_task_and_role()
    {
        using var fake = new FakeModelServer(Reply);
        var gateway = new GatewayOptions(ModelProvider.OpenAICompatible, fake.Url, "test-key", "f", "standard", "s");
        await new FactoryAgents(gateway, new RoleAllowListPolicy(), new MemoryLedger())
            .Create(Scope, ModelTier.Standard, "plan", []).RunAsync("go");

        var body = JsonNode.Parse(fake.Bodies.Single())!;
        Assert.Equal($"run-1/T-001/{Roles.TaskPlanner}", (string?)body["prompt_cache_key"]);
    }

    [Fact]
    public async Task A_client_used_outside_an_agent_sends_no_key()
    {
        using var fake = new FakeModelServer(Reply);
        var gateway = new GatewayOptions(ModelProvider.OpenAICompatible, fake.Url, "test-key", "f", "standard", "s");
        await gateway.ChatClientFor(ModelTier.Standard).GetResponseAsync("hi");
        Assert.Null(JsonNode.Parse(fake.Bodies.Single())!["prompt_cache_key"]);
    }

    /// Anthropic has no such field; the key must not reach its wire.
    [Fact]
    public async Task An_Anthropic_agent_sends_no_key()
    {
        const string ok = """
            {"id":"m","type":"message","role":"assistant","model":"x","content":[{"type":"text","text":"ok"}],
             "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
            """;
        using var fake = new FakeModelServer(ok);
        var gateway = new GatewayOptions(ModelProvider.Anthropic, fake.Url, "test-key", "f", "s", "m");
        await new FactoryAgents(gateway, new RoleAllowListPolicy(), new MemoryLedger())
            .Create(Scope, ModelTier.Standard, "plan", []).RunAsync("go");

        Assert.DoesNotContain("prompt_cache_key", fake.Bodies.Single());
        Assert.DoesNotContain("factory:", fake.Bodies.Single());
    }

    /// Gemini caches implicitly; behind LiteLLM or Gemini's OpenAI endpoint a cache read comes back as
    /// prompt_tokens_details.cached_tokens, inside prompt_tokens. It reaches the turn row as cache_read.
    [Fact]
    public async Task A_cache_read_reported_the_OpenAI_way_reaches_the_turn_usage()
    {
        const string gemini = """
            {"id":"c1","object":"chat.completion","created":1,"model":"gemini-2.5-pro",
             "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":1200,"completion_tokens":5,"total_tokens":1205,"prompt_tokens_details":{"cached_tokens":1024}}}
            """;
        using var fake = new FakeModelServer(gemini);
        var gateway = new GatewayOptions(ModelProvider.OpenAICompatible, fake.Url, "test-key", "f", "gemini-2.5-pro", "s");
        var u = (await gateway.ChatClientFor(ModelTier.Standard).GetResponseAsync("hi")).Usage!;

        Assert.Equal(1200, u.InputTokenCount);
        Assert.Equal((1024L, 0L), TurnUsage.Cache(u));
        var payload = new JsonObject();
        TurnUsage.AddCache(payload, u);
        Assert.Equal(1024, (long)payload["cache_read"]!);
    }
}
