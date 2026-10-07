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

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
