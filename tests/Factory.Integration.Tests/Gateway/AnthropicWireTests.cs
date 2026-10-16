using System.Net;
using System.Net.Sockets;
using System.Text;
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
        private readonly HttpListener _listener = new();
        public List<string> Bodies { get; } = [];
        public Uri Url { get; }

        public FakeAnthropic(string reply)
        {
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop();
            Url = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(Url.ToString());
            _listener.Start();
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
}
