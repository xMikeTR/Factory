using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Factory.Harness;
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

    /// A one-shot fake: answers every POST with `reply` and keeps each request body.
    internal sealed class FakeAnthropic : IDisposable
    {
        private readonly HttpListener _listener;
        public List<string> Bodies { get; } = [];
        public Uri Url { get; }

        public FakeAnthropic(string reply)
        {
            // A free port can be taken by a parallel test between the probe and the bind, so a failed bind tries another.
            for (var attempt = 1; ; attempt++)
            {
                var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try { listener.Start(); }
                catch (HttpListenerException) when (attempt < 5) { listener.Close(); continue; }
                (_listener, Url) = (listener, new Uri($"http://127.0.0.1:{port}/"));
                break;
            }
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    using (var r = new StreamReader(ctx.Request.InputStream)) Bodies.Add(await r.ReadToEndAsync());
                    var bytes = Encoding.UTF8.GetBytes(reply);
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose() => _listener.Close();
    }

    internal static IChatClient Client(Uri url) =>
        new GatewayOptions(ModelProvider.Anthropic, url, "test-key", "f", "s", "m").ChatClientFor(Factory.Core.ModelTier.Standard);

    /// S2 reports tokens_in as all input and splits off the cached part; the adapter already counts that way.
    [Fact]
    public async Task Input_counts_cached_tokens_with_reads_and_writes_reported_apart()
    {
        using var fake = new FakeAnthropic(Reply);
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

    /// Tools, instructions and the brief never change within a turn, so every call after the first reads them from cache.
    [Fact]
    public async Task The_brief_is_a_cache_breakpoint_and_nothing_else_is_while_no_result_is_elided()
    {
        using var fake = new FakeAnthropic(Ok);
        var messages = Turn(2);
        await Client(fake.Url).GetResponseAsync(messages, new ChatOptions { Instructions = "role instructions" });
        Assert.Equal(["user:text"], Marked(fake.Bodies.Single()));
        Assert.All(messages.SelectMany(m => m.Contents), c => Assert.Null(c.AdditionalProperties));
    }

    /// A stub never changes once made, so the newest stub is a breakpoint: the next call reads everything before it.
    /// A breakpoint at the end would be rewritten on every call, because each call elides one more result.
    [Fact]
    public async Task The_newest_elided_tool_result_is_a_cache_breakpoint()
    {
        using var fake = new FakeAnthropic(Ok);
        var client = new TrimmingChatClient(Client(fake.Url), keepRecent: 5);
        await client.GetResponseAsync(Turn(7));
        await client.GetResponseAsync(Turn(8));
        Assert.Equal(["user:text", "user:tool_result:c2"], Marked(fake.Bodies[0]));
        Assert.Equal(["user:text", "user:tool_result:c3"], Marked(fake.Bodies[1]));
    }
}
