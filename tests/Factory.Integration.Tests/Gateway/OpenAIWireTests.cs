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

    /// Claude behind LiteLLM caches only where a request says so: with FACTORY_GATEWAY_CACHE_CONTROL=on the brief and
    /// the end of the request carry Anthropic's breakpoint, at message level, where LiteLLM reads it for user and tool
    /// messages alike. The same two places PromptCache marks on the native Anthropic wire.
    [Fact]
    public async Task With_cache_control_on_the_brief_and_the_last_tool_result_carry_a_breakpoint()
    {
        using var fake = new FakeModelServer(Reply);
        var gateway = GatewayOptions.FromEnvironment(new Dictionary<string, string>
        {
            ["FACTORY_GATEWAY_KEY"] = "k", ["FACTORY_GATEWAY_URL"] = fake.Url.ToString(), ["FACTORY_GATEWAY_CACHE_CONTROL"] = "on",
        });
        List<ChatMessage> history =
        [
            new(ChatRole.System, "plan"),
            new(ChatRole.User, "the brief"),
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "read_file"), new FunctionCallContent("c2", "read_file")]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "one"), new FunctionResultContent("c2", "two")]),
        ];
        await gateway.ChatClientFor(ModelTier.Standard).GetResponseAsync(history);

        var marked = JsonNode.Parse(fake.Bodies.Single())!["messages"]!.AsArray()
            .Select(m => ((string?)m!["role"], (string?)m["cache_control"]?["type"]));
        Assert.Equal([("system", null), ("user", "ephemeral"), ("assistant", null), ("tool", null), ("tool", "ephemeral")], marked);
    }

    private static async Task<string> BodyForAsync(string model, string? setting)
    {
        using var fake = new FakeModelServer(Reply);
        var env = new Dictionary<string, string>
        {
            ["FACTORY_GATEWAY_KEY"] = "k", ["FACTORY_GATEWAY_URL"] = fake.Url.ToString(), ["FACTORY_MODEL_STANDARD"] = model,
        };
        if (setting is not null) env["FACTORY_GATEWAY_CACHE_CONTROL"] = setting;
        await GatewayOptions.FromEnvironment(env).ChatClientFor(ModelTier.Standard)
            .GetResponseAsync([new ChatMessage(ChatRole.User, "the brief")]);
        return fake.Bodies.Single();
    }

    /// Any model can sit behind the endpoint (Vertex Model Garden, LiteLLM): the breakpoint goes where the model is
    /// Claude, which caches only where asked. Gemini and OpenAI cache the prefix themselves and get nothing extra.
    [Theory]
    [InlineData("claude-sonnet-4-5@20250929", null, true)]
    [InlineData("publishers/anthropic/models/claude-opus-4-1", "auto", true)]
    [InlineData("gemini-2.5-pro", null, false)]
    [InlineData("gpt-5", null, false)]
    [InlineData("standard", null, false)]
    [InlineData("gemini-2.5-pro", "on", true)]
    [InlineData("claude-sonnet-4-5", "off", false)]
    public async Task Cache_control_follows_the_model_unless_set(string model, string? setting, bool sent) =>
        Assert.Equal(sent, (await BodyForAsync(model, setting)).Contains("cache_control"));

    /// A model the endpoint does not take cache_control for: left off.
    [Fact]
    public async Task Without_cache_control_no_breakpoint_reaches_the_wire()
    {
        using var fake = new FakeModelServer(Reply);
        var gateway = new GatewayOptions(ModelProvider.OpenAICompatible, fake.Url, "test-key", "f", "standard", "s");
        await new FactoryAgents(gateway, new RoleAllowListPolicy(), new MemoryLedger())
            .Create(Scope, ModelTier.Standard, "plan", []).RunAsync("go");

        Assert.DoesNotContain("cache_control", fake.Bodies.Single());
    }

    [Fact]
    public void Cache_control_is_auto_on_or_off()
    {
        var env = new Dictionary<string, string> { ["FACTORY_GATEWAY_KEY"] = "k", ["FACTORY_GATEWAY_CACHE_CONTROL"] = "yes" };
        Assert.Throws<FactoryRejectedException>(() => GatewayOptions.FromEnvironment(env));
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
