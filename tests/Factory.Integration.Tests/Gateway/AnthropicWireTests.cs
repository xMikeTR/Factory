using System.Text.Json.Nodes;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Microsoft.Extensions.AI;
using Xunit;

namespace Factory.Integration.Tests.Gateway;

/// What S2 sends to and reads from the Anthropic Messages API, against a local fake of it.
public class AnthropicWireTests
{
    private const string Reply = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-test","content":[{"type":"text","text":"ok"}],
         "stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":10,"cache_read_input_tokens":100,"cache_creation_input_tokens":50,"output_tokens":5}}
        """;

    internal static IChatClient Client(Uri url) =>
        new GatewayOptions(ModelProvider.Anthropic, url, "test-key", "f", "s", "m").ChatClientFor(Factory.Core.ModelTier.Standard);

    /// S2 reports tokens_in as all input and splits off the cached part; the adapter already counts that way.
    [Fact]
    public async Task Input_counts_cached_tokens_with_reads_and_writes_reported_apart()
    {
        using var fake = new FakeModelServer(Reply);
        var u = (await Client(fake.Url).GetResponseAsync("hi")).Usage!;
        Assert.Equal(160, u.InputTokenCount);
        Assert.Equal(100, u.CachedInputTokenCount);
        Assert.Equal(50, u.AdditionalCounts!["CacheCreationInputTokens"]);
        Assert.Equal((100L, 50L), TurnUsage.Cache(u));
    }

    private const string Ok = """
        {"id":"m","type":"message","role":"assistant","model":"x","content":[{"type":"text","text":"ok"}],
         "stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
        """;

    /// The blocks of a sent request that carry cache_control, as "role:type" in order.
    private static List<string> Marked(string body) =>
        JsonNode.Parse(body)!["messages"]!.AsArray()
            .SelectMany(m => m!["content"]!.AsArray().Where(c => c!["cache_control"] is not null)
                .Select(c => $"{m["role"]}:{c!["type"]}{(c["tool_use_id"] is { } id ? ":" + id : "")}"))
            .ToList();

    private static List<ChatMessage> Turn(int toolResults)
    {
        var list = new List<ChatMessage> { new(ChatRole.User, "the brief") };
        for (var i = 1; i <= toolResults; i++)
        {
            list.Add(new(ChatRole.Assistant, [new FunctionCallContent($"c{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{i}" })]));
            list.Add(new(ChatRole.Tool, [new FunctionResultContent($"c{i}", new string('x', 50))]));
        }
        return list;
    }

    /// The brief is a breakpoint (tools, instructions and the brief never change within a turn), and so is the end:
    /// results are elided in batches, so the next call starts with this whole request and reads it from cache.
    [Fact]
    public async Task The_brief_and_the_end_of_the_request_are_cache_breakpoints()
    {
        using var fake = new FakeModelServer(Ok);
        var messages = Turn(2);
        await Client(fake.Url).GetResponseAsync(messages, new ChatOptions { Instructions = "role instructions" });
        Assert.Equal(["user:text", "user:tool_result:c2"], Marked(fake.Bodies.Single()));
        Assert.All(messages.SelectMany(m => m.Contents), c => Assert.Null(c.AdditionalProperties));
    }

    /// Between elisions each request extends the one before, so its end breakpoint is the next call's cache hit.
    /// Stubs carry no breakpoint of their own: the end breakpoint after them already covers them.
    [Fact]
    public async Task After_an_elision_only_the_brief_and_the_end_are_breakpoints()
    {
        using var fake = new FakeModelServer(Ok);
        var client = new TrimmingChatClient(Client(fake.Url), keepRecent: 5, batch: 5);
        await client.GetResponseAsync(Turn(10));
        await client.GetResponseAsync(Turn(11));
        Assert.Equal(["user:text", "user:tool_result:c10"], Marked(fake.Bodies[0]));
        Assert.Equal(["user:text", "user:tool_result:c11"], Marked(fake.Bodies[1]));
    }

    [Fact]
    public async Task A_request_with_only_the_brief_marks_it_once()
    {
        using var fake = new FakeModelServer(Ok);
        await Client(fake.Url).GetResponseAsync([new ChatMessage(ChatRole.User, "the brief")]);
        Assert.Equal(["user:text"], Marked(fake.Bodies.Single()));
    }
}
