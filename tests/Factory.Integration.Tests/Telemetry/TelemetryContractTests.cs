using Factory.Core;
using Factory.Harness;
using Factory.Harness.Telemetry;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Factory.S2.Teams;
using Microsoft.Extensions.AI;
using Xunit;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Telemetry;

/// The attribute contract System 4 and Langfuse read: every task span says which system, element,
/// task, decision and attempt it belongs to, carries the `designc` tag and the `task_passed` score,
/// and model calls nest under it with their content off unless asked for.
public class TelemetryContractTests
{
    private const string NewFile = "src/Pricing.Data/QuoteStatus.cs";
    private const string Brief = "secret-brief-text-0042";
    private static readonly IReadOnlyDictionary<string, DecisionAnswer> NoAnswers = new Dictionary<string, DecisionAnswer>();

    private static ScriptedChatClient PassingScript() => new(
        Say("Approach: add QuoteStatus.cs"),
        Call("declare_done", new { summary = "verify block already fails" }), Say("done"),
        Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
        Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));

    [Fact]
    public async Task A_task_span_carries_the_attribute_contract_and_the_score()
    {
        using var spans = new SpanCollector();
        var task = Plans.Covering("T-001", ["D-0042"], Plans.Auto("file exists", $"test -f {NewFile}"));
        using var h = await TeamHarness.CreateAsync(PassingScript(), new Limits(8, 3));

        var result = await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(task), NoAnswers, default);

        Assert.Equal(TeamStatus.Passed, result.Status);
        var span = Assert.Single(spans.Spans, s => s.OperationName == "task T-001");
        Assert.Equal("S2", span.GetTagItem(FactoryTelemetry.Attr.System));
        Assert.Equal(TeamHarness.RunId, span.GetTagItem(FactoryTelemetry.Attr.RunId));
        Assert.Equal("T-001", span.GetTagItem(FactoryTelemetry.Attr.Task));
        Assert.Equal(new[] { "Pricing Platform/Quotes DB" }, span.GetTagItem(FactoryTelemetry.Attr.Element));
        Assert.Equal(new[] { "D-0042" }, span.GetTagItem(FactoryTelemetry.Attr.Decision));
        Assert.Equal(1, span.GetTagItem(FactoryTelemetry.Attr.Attempt));
        Assert.Equal(new[] { FactoryTelemetry.Tag }, span.GetTagItem(FactoryTelemetry.Attr.LangfuseTags));
        Assert.Equal(TeamHarness.RunId, span.GetTagItem(FactoryTelemetry.Attr.LangfuseSession));
        Assert.Equal(1, span.GetTagItem(FactoryTelemetry.Attr.TaskPassed));
        Assert.Contains(span.Events, e => e.Name == "score" && e.Tags.Any(t => t is { Key: "name", Value: "task_passed" }));

        // Each model call and each tool call is a gen_ai span under the task span.
        var genAi = spans.Spans.Where(s => IsUnder(s, span)).Select(s => s.GetTagItem("gen_ai.operation.name") as string).OfType<string>().ToList();
        Assert.Equal(6, genAi.Count(op => op == "chat"));
        Assert.Equal(3, genAi.Count(op => op == "execute_tool"));

        // Ledger rows written inside the task link to its trace.
        Assert.All(h.Ledger.Events.Where(e => e.TaskId == "T-001"), e => Assert.Equal(spans.TraceId.ToString(), e.TraceId));
    }

    [Fact]
    public async Task A_failed_task_scores_zero_with_its_last_attempt()
    {
        using var spans = new SpanCollector();
        var never = Plans.Task("T-001", Plans.Auto("never passes", "test -f does-not-exist"));
        var model = new ScriptedChatClient(
            Say("Approach: try"),
            Call("declare_done", new { summary = "tests" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "// 1\n" }), Call("declare_done", new { summary = "1" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "// 2\n" }), Call("declare_done", new { summary = "2" }), Say("done"));
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 2));

        await new TeamRunner(h.Services, h.Handover, TeamHarness.RunId).RunAsync(TeamHarness.TeamOf(never), NoAnswers, default);

        var span = Assert.Single(spans.Spans, s => s.OperationName == "task T-001");
        Assert.Equal(0, span.GetTagItem(FactoryTelemetry.Attr.TaskPassed));
        Assert.Equal(2, span.GetTagItem(FactoryTelemetry.Attr.Attempt));
        Assert.Empty((string[])span.GetTagItem(FactoryTelemetry.Attr.Decision)!);
    }

    [Fact]
    public async Task A_run_is_one_trace_and_every_ledger_row_links_to_it()
    {
        using var spans = new SpanCollector();
        var model = new ScriptedChatClient();
        using var h = await TeamHarness.CreateAsync(model, new Limits(8, 3));
        model.Enqueue(
            SubmitPlan(Workflow.S2WorkflowTests.PlanFor(h.Handover)), Say("submitted"),
            Say("Approach: add QuoteStatus.cs"),
            Call("declare_done", new { summary = "test already on main" }), Say("done"),
            Call("write_file", new { path = NewFile, content = "public enum QuoteStatus { Draft }\n" }),
            Call("declare_done", new { summary = "added QuoteStatus" }), Say("done"));

        await WorkflowHost.RunAsync(S2Workflow.Build(h.Services), new StartRun(TeamHarness.RunId, h.WriteHandoverJson()), new Approve(), default);

        var run = Assert.Single(spans.Spans, s => s.OperationName == $"run {TeamHarness.RunId}");
        Assert.Equal(("S2", TeamHarness.RunId), (run.GetTagItem(FactoryTelemetry.Attr.System), run.GetTagItem(FactoryTelemetry.Attr.RunId)));
        Assert.Equal(new[] { FactoryTelemetry.Tag }, run.GetTagItem(FactoryTelemetry.Attr.LangfuseTags));
        Assert.True(IsUnder(Assert.Single(spans.Spans, s => s.OperationName == "task T-001"), run));
        Assert.NotEmpty(h.Ledger.Events);
        Assert.All(h.Ledger.Events, e => Assert.Equal(run.TraceId.ToString(), e.TraceId));
    }

    private sealed class Approve : IHumanChannel
    {
        public Task<MergeApprovalResponse> ApproveAsync(MergeApprovalRequest request, CancellationToken ct) =>
            Task.FromResult(new MergeApprovalResponse(request.RunId, true, "test-approver"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Message_content_reaches_a_span_only_when_capture_is_on(bool capture)
    {
        using var spans = new SpanCollector();
        var model = new ScriptedChatClient(Say("reply-text-0042"));
        var agent = new FactoryAgents(_ => model, new RoleAllowListPolicy(), new MemoryLedger(), capture)
            .Create(new AgentScope("run-1", Roles.Planner), ModelTier.Standard, "plan", []);

        await agent.RunAsync(Brief);

        var chat = Assert.Single(spans.Spans, s => s.GetTagItem("gen_ai.operation.name") is "chat");
        Assert.Equal("100", chat.GetTagItem("gen_ai.usage.input_tokens")?.ToString());
        var text = string.Join('\n', chat.TagObjects.Select(t => $"{t.Key}={t.Value}").Concat(chat.Events.SelectMany(e => e.Tags.Select(t => $"{t.Key}={t.Value}"))));
        Assert.Equal(capture, text.Contains(Brief));
        Assert.Equal(capture, text.Contains("reply-text-0042"));
    }

    [Fact]
    public void Without_an_OTLP_endpoint_there_is_no_exporter()
    {
        Assert.Null(FactoryTelemetry.CreateTracerProvider(new Dictionary<string, string>()));
        using var provider = FactoryTelemetry.CreateTracerProvider(new Dictionary<string, string> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1" });
        Assert.NotNull(provider);
        Assert.False(FactoryTelemetry.CaptureContent(new Dictionary<string, string>()));
        Assert.True(FactoryTelemetry.CaptureContent(new Dictionary<string, string> { ["FACTORY_OTEL_CAPTURE_CONTENT"] = "1" }));
    }

    private static bool IsUnder(System.Diagnostics.Activity s, System.Diagnostics.Activity ancestor)
    {
        for (var p = s.Parent; p is not null; p = p.Parent) if (p.SpanId == ancestor.SpanId) return true;
        return false;
    }
}
