using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Xunit;

namespace Factory.Integration.Tests.Agents;

/// Shape only: whatever routes the turn, every context gets a kind, a tier, a reason and a decider.
public abstract class ConfiguratorContract
{
    protected abstract IConfigurator Configurator { get; }

    public static TheoryData<TurnContext> Contexts() =>
    [
        new("T-001", 0, 0, false, false, 0),
        new("T-001", 3, 1, true, true, 0),
        new("T-001", 5, 2, true, true, 1),
        new("T-001", 6, 0, true, false, 4),
    ];

    [Theory]
    [MemberData(nameof(Contexts))]
    public async Task Every_turn_gets_a_complete_decision(TurnContext ctx)
    {
        var d = await Configurator.DecideAsync(ctx);

        Assert.True(Enum.IsDefined(d.Kind));
        Assert.NotNull(d.Tier);
        Assert.False(string.IsNullOrWhiteSpace(d.Reason));
        Assert.False(string.IsNullOrWhiteSpace(d.DecidedBy));
    }
}

public class HeuristicConfiguratorContract : ConfiguratorContract
{
    protected override IConfigurator Configurator { get; } = new HeuristicConfigurator();
}

public class LayaConfiguratorContract : ConfiguratorContract
{
    protected override IConfigurator Configurator { get; } =
        new LayaConfigurator(new HttpClient(LayaStub.Answer("""{"kind":"act","tier":"standard","reason":"routine","confidence":0.9}""")) { BaseAddress = LayaStub.Url });
}

/// An HttpMessageHandler standing in for the sidecar; records what was sent.
public sealed class LayaStub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public static readonly Uri Url = new("http://laya.test/");
    public List<(string Path, string Body)> Requests { get; } = [];

    public static LayaStub Answer(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add((request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
        return respond(request);
    }
}

public class ConfiguratorTests
{
    private static readonly TurnContext Ctx = new("T-001", 3, 1, true, true, 0);
    private static readonly IConfigurator Heuristic = new HeuristicConfigurator();

    private static LayaConfigurator Laya(LayaStub stub) => new(new HttpClient(stub) { BaseAddress = LayaStub.Url });

    [Fact]
    public async Task Laya_posts_the_turn_context_and_reads_back_its_decision()
    {
        var stub = LayaStub.Answer("""{"kind":"plan","tier":"frontier","reason":"re-plan","confidence":0.82}""");

        var d = await Laya(stub).DecideAsync(Ctx);

        Assert.Equal((TurnKind.Plan, ModelTier.Frontier, "re-plan", "laya", 0.82), (d.Kind, d.Tier, d.Reason, d.DecidedBy, d.Confidence));
        var (path, body) = Assert.Single(stub.Requests);
        Assert.Equal("/decide", path);
        using var sent = JsonDocument.Parse(body);
        Assert.Equal("T-001", sent.RootElement.GetProperty("taskId").GetString());
        Assert.True(sent.RootElement.GetProperty("lastVerifyFailed").GetBoolean());
    }

    [Fact]
    public async Task A_confident_Laya_decides()
    {
        var fallback = new FallbackConfigurator(Laya(LayaStub.Answer("""{"kind":"act","tier":"small","reason":"tiny fix","confidence":0.9}""")), Heuristic, 0.7);

        var d = await fallback.DecideAsync(Ctx);

        Assert.Equal(("laya", ModelTier.Small), (d.DecidedBy, d.Tier));
    }

    [Fact]
    public async Task Below_the_confidence_threshold_the_heuristic_decides_and_says_why()
    {
        var fallback = new FallbackConfigurator(Laya(LayaStub.Answer("""{"kind":"act","tier":"small","reason":"tiny fix","confidence":0.4}""")), Heuristic, 0.7);

        var d = await fallback.DecideAsync(Ctx);

        Assert.Equal("heuristic", d.DecidedBy);
        Assert.Equal((await Heuristic.DecideAsync(Ctx)).Kind, d.Kind);
        Assert.Contains("laya confidence 0.4 < 0.7", d.Reason);
    }

    [Theory]
    [InlineData("""{"error":"laya not installed"}""", HttpStatusCode.ServiceUnavailable)]
    [InlineData("""{"kind":"dance","tier":"standard","reason":"?","confidence":0.9}""", HttpStatusCode.OK)]
    [InlineData("not json", HttpStatusCode.OK)]
    public async Task An_unavailable_or_garbled_Laya_falls_back_to_the_heuristic(string body, HttpStatusCode status)
    {
        var d = await new FallbackConfigurator(Laya(LayaStub.Answer(body, status)), Heuristic, 0.7).DecideAsync(Ctx);

        Assert.Equal("heuristic", d.DecidedBy);
        Assert.Contains("laya unavailable", d.Reason);
    }

    [Fact]
    public async Task A_sidecar_that_is_down_falls_back_to_the_heuristic()
    {
        var port = FreePort();
        var laya = new LayaConfigurator(new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(2) });

        var d = await new FallbackConfigurator(laya, Heuristic, 0.7).DecideAsync(Ctx);

        Assert.Equal("heuristic", d.DecidedBy);
        Assert.Contains("laya unavailable", d.Reason);
    }

    [Fact]
    public async Task Cancellation_is_not_mistaken_for_an_unavailable_sidecar()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var fallback = new FallbackConfigurator(Laya(LayaStub.Answer("{}")), Heuristic, 0.7);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fallback.DecideAsync(Ctx, cts.Token).AsTask());
    }

    [Fact]
    public async Task Against_the_real_sidecar_without_Laya_installed_the_heuristic_decides()
    {
        var sidecar = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/laya-sidecar/server.py"));
        var port = FreePort();
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python3", [sidecar, "--port", port.ToString()])
            { RedirectStandardOutput = true })!;
        try
        {
            await proc.StandardOutput.ReadLineAsync(); // "laya-sidecar on ..." once listening
            var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };

            var d = await new FallbackConfigurator(new LayaConfigurator(http), Heuristic, 0.7).DecideAsync(Ctx);

            Assert.Equal("heuristic", d.DecidedBy);
            Assert.True(d.Reason.Contains("503"), d.Reason);
        }
        finally { proc.Kill(); }
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }
}
