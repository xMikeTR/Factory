using Factory.Core;
using Factory.Harness;
using Factory.Harness.Sandbox;
using Xunit;

namespace Factory.Integration.Tests.Workspace;

public class ScopedWorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ws-").FullName;

    [Fact]
    public async Task Commands_run_in_the_sandbox_without_host_secrets()
    {
        Environment.SetEnvironmentVariable("FACTORY_GATEWAY_KEY", "sekrit-gateway");
        try
        {
            var ws = new ScopedWorkspace(_root, [], [], ["printenv"], new TurnSignals(), new LocalSandbox());
            var output = await ws.RunCommand("printenv");
            Assert.StartsWith("exit 0", output);
            Assert.DoesNotContain("sekrit-gateway", output);
            Assert.Contains("PATH=", output);
        }
        finally { Environment.SetEnvironmentVariable("FACTORY_GATEWAY_KEY", null); }
    }

    [Fact]
    public async Task Without_a_sandbox_commands_are_refused()
    {
        var ws = new ScopedWorkspace(_root, [], [], ["printenv"], new TurnSignals());
        Assert.StartsWith("DENIED", await ws.RunCommand("printenv"));
    }

    [Fact]
    public async Task The_reviewer_can_read_and_submit_findings_but_not_write_run_or_park()
    {
        var signals = new TurnSignals();
        var tools = new ScopedWorkspace(_root, [], [], [], signals).ReviewerTools();
        Assert.Equal(["list_files", "read_file", "submit_review"], tools.Select(t => t.Name).Order());
        var submit = tools.OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "submit_review");

        var refused = await submit.InvokeAsync(new()
        {
            ["findings"] = System.Text.Json.JsonDocument.Parse("""[{"category":"style","file":"src/A.cs","line":3,"message":"x"}]""").RootElement.Clone(),
        });
        Assert.Null(signals.Review);
        Assert.Contains("category 'style'", refused?.ToString());

        await submit.InvokeAsync(new()
        {
            ["findings"] = System.Text.Json.JsonDocument.Parse("""[{"category":"weak_tests","file":"tests/A.cs","line":7,"message":"asserts nothing"}]""").RootElement.Clone(),
        });
        var finding = Assert.Single(signals.Review!);
        Assert.Equal((FindingCategory.WeakTests, "tests/A.cs", 7), (finding.Category, finding.File, finding.Line));
    }

    [Fact]
    public async Task Submit_approach_records_the_note_and_the_validated_selection_and_reports_rejections()
    {
        var signals = new TurnSignals();
        var ws = new ScopedWorkspace(_root, [], [], [], signals);
        var tools = ws.TaskPlannerTools(noms =>
        {
            var ok = noms.Where(n => n.Path != "src/Missing.cs").ToList();
            return new Selection(
                Microsoft.FSharp.Collections.ListModule.OfSeq(ok.Select(n => new ContextEntry(n.Path, ContextSource.NewNominated(n.Reason)))),
                Microsoft.FSharp.Collections.ListModule.OfSeq(noms.Except(ok).Select(n => Tuple.Create(n.Path, "does not exist"))));
        });
        Assert.Contains(tools, t => t.Name == "raise_needs_decision");
        var submit = tools.OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "submit_approach");

        var answer = await submit.InvokeAsync(new()
        {
            ["note"] = "change Quote.cs first",
            ["context"] = System.Text.Json.JsonDocument.Parse("""[{"path":"src/A.cs","reason":"calls Quote"},{"path":"src/Missing.cs","reason":"x"}]""").RootElement.Clone(),
        });

        Assert.Equal("change Quote.cs first", signals.ApproachNote);
        Assert.Equal(["src/A.cs"], signals.ContextSelection!.Entries.Select(e => e.Path));
        Assert.Contains("src/Missing.cs", answer?.ToString());
        Assert.Contains("does not exist", answer?.ToString());
    }

    [Fact]
    public async Task Submit_plan_takes_the_plan_schema_records_each_plan_and_returns_the_validators_errors()
    {
        var schema = System.Text.Json.JsonDocument.Parse("""{"$schema":"x","$id":"y","type":"object","required":["tasks"],"properties":{"tasks":{"type":"array"}}}""").RootElement;
        var signals = new TurnSignals();
        var ws = new ScopedWorkspace(_root, [], [], [], signals);
        var submit = ws.PlannerTools(schema, plan => plan.Contains("\"bad\"") ? ["T-001 is bad"] : []).OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "submit_plan");

        Assert.Equal("array", submit.JsonSchema.GetProperty("properties").GetProperty("tasks").GetProperty("type").GetString());
        Assert.False(submit.JsonSchema.TryGetProperty("$schema", out _));

        var bad = await submit.InvokeAsync(new() { ["tasks"] = new[] { "bad" } });
        Assert.Contains("T-001 is bad", bad?.ToString());
        Assert.Contains("bad", signals.SubmittedPlan);

        var good = await submit.InvokeAsync(new() { ["tasks"] = new[] { "good" } });
        Assert.StartsWith("Accepted", good?.ToString());
        Assert.Contains("good", signals.SubmittedPlan);

        await submit.InvokeAsync(new() { ["tasks"] = new[] { "bad" } });
        var refused = await submit.InvokeAsync(new() { ["tasks"] = new[] { "good" } });
        Assert.StartsWith("DENIED", refused?.ToString());
        Assert.Contains("bad", signals.SubmittedPlan); // the last plan the validator saw stands
    }

    [Fact]
    public async Task Raise_needs_decision_refuses_a_question_without_a_real_choice()
    {
        var signals = new TurnSignals();
        var ws = new ScopedWorkspace(_root, [], [], [], signals);
        var raise = ws.ReadOnlyTools().OfType<Microsoft.Extensions.AI.AIFunction>().Single(t => t.Name == "raise_needs_decision");
        var a = new { id = "A", summary = "3 attempts", consequence = "a 4th conflict fails the write" };
        var b = new { id = "B", summary = "no limit", consequence = "writes may spin" };

        var one = await raise.InvokeAsync(new() { ["question"] = "Retry limit?", ["options"] = new[] { a } });
        Assert.Contains("at least two options", one?.ToString());
        Assert.Null(signals.NeedsDecision);

        var unknown = await raise.InvokeAsync(new() { ["question"] = "Retry limit?", ["options"] = new[] { a, b }, ["recommended"] = "C" });
        Assert.Contains("recommended 'C'", unknown?.ToString());
        Assert.Null(signals.NeedsDecision);

        var ok = await raise.InvokeAsync(new() { ["question"] = "Retry limit?", ["options"] = new[] { a, b }, ["recommended"] = "A" });
        Assert.StartsWith("Recorded", ok?.ToString());
        Assert.Equal("Retry limit?", signals.NeedsDecision?.Question);
        Assert.Equal(["A", "B"], signals.NeedsDecision!.Options.Select(o => o.Id));
        Assert.Equal("A", signals.NeedsDecision.Recommended);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
