using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Factory.Core;
using Factory.Harness.Telemetry;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Teams;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Live;

/// Opt-in: FACTORY_LIVE_LANGFUSE=1 with LANGFUSE_PUBLIC_KEY / LANGFUSE_SECRET_KEY (and
/// LANGFUSE_HOST, default http://localhost:3000), e.g. `. ~/Desktop/Coding/langfuse-local/env.sh`.
/// Exports one scripted task over plain OTLP and reads the trace back from the Langfuse API.
public class LiveLangfuseTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("FACTORY_LIVE_LANGFUSE") == "1";
    private const string NewFile = "src/Pricing.Data/QuoteStatus.cs";

    [Fact]
    public async Task A_task_trace_reaches_Langfuse_with_the_attribute_contract()
    {
        if (!Enabled) return; // opt-in
        var host = (Environment.GetEnvironmentVariable("LANGFUSE_HOST") is { Length: > 0 } h0 ? h0 : "http://localhost:3000").TrimEnd('/');
        var secret = Environment.GetEnvironmentVariable("LANGFUSE_SECRET_KEY")!;
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Environment.GetEnvironmentVariable("LANGFUSE_PUBLIC_KEY")}:{secret}"));

        string traceId;
        using (var tracing = FactoryTelemetry.CreateTracerProvider(new Dictionary<string, string>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"{host}/api/public/otel",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = $"Authorization=Basic {basic}",
        }))
        {
            var model = new ScriptedChatClient(
                Say("Approach: add QuoteStatus.cs"),
                Call("declare_done", new { summary = "verify block already fails" }), Say("done"),
                Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
                Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));
            using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
            var task = Plans.Covering("T-001", ["D-0042"], Plans.Auto("file exists", $"test -f {NewFile}"));
            var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(task), new Dictionary<string, DecisionAnswer>(), default);
            Assert.Equal(TeamStatus.Passed, result.Status);
            traceId = h.Ledger.Events.First(e => e.TaskId == "T-001").TraceId!;
        } // disposing flushes the exporter

        using var http = new HttpClient { BaseAddress = new Uri(host) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
        // Langfuse v4 (events_only) reads observations through /api/public/v2; ingestion is async.
        string? body = null;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            var r = await http.GetAsync($"/api/public/v2/observations?traceId={traceId}&fields=core,basic,metadata&limit=100");
            if (r.IsSuccessStatusCode && (body = await r.Content.ReadAsStringAsync()).Contains("task T-001")) break;
            body = null;
            await Task.Delay(2000);
        }
        Assert.True(body is not null, $"trace {traceId} did not reach Langfuse at {host}");

        using var doc = JsonDocument.Parse(body);
        var observations = doc.RootElement.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(6, observations.Count(o => o.GetProperty("type").GetString() == "GENERATION"));
        var taskObs = observations.Single(o => o.GetProperty("name").GetString() == "task T-001");
        Assert.Equal(TeamHarness.RunId, taskObs.GetProperty("sessionId").GetString());
        var meta = taskObs.GetProperty("metadata");
        Assert.Equal("S2", meta.GetProperty("attributes.factory.system").GetString());
        Assert.Equal("T-001", meta.GetProperty("attributes.factory.task").GetString());
        Assert.Equal("D-0042", meta.GetProperty("attributes.factory.decision")[0].GetString());
        Assert.Equal("Pricing Platform/Quotes DB", meta.GetProperty("attributes.factory.element")[0].GetString());
        Assert.Equal(1, meta.GetProperty("attributes.factory.attempt").GetInt32());
        Assert.Equal(1, meta.GetProperty("attributes.factory.score.task_passed").GetInt32());
        Assert.Equal(FactoryTelemetry.Tag, meta.GetProperty("attributes.langfuse.trace.tags")[0].GetString());
        Assert.DoesNotContain(secret, body);
    }
}
