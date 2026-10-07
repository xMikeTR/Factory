using Xunit;
using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Microsoft.Extensions.AI;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Agents;

public class FactoryAgentsTests
{
    private readonly MemoryLedger _ledger = new();
    private bool _wrote;

    private IList<AITool> Tools() =>
    [
        AIFunctionFactory.Create((string path) => { _wrote = true; return $"wrote {path}"; }, "write_file"),
        AIFunctionFactory.Create((string path) => $"contents of {path}", "read_file"),
    ];

    private static readonly AgentScope Scope = new("run-1", Roles.TaskPlanner, "team-T-001", "T-001");

    [Fact]
    public async Task Denied_tool_is_not_invoked_and_is_ledgered()
    {
        var model = new ScriptedChatClient(Call("write_file", new { path = "src/x.cs" }), Say("ok"));
        var agent = new FactoryAgents(_ => model, new RoleAllowListPolicy(), _ledger).Create(Scope, ModelTier.Standard, "plan", Tools());

        await agent.RunAsync("go");

        Assert.False(_wrote);
        var row = Assert.Single(_ledger.Events, e => e.Kind == LedgerKinds.ToolCall);
        Assert.Equal("denied", row.Outcome);
        Assert.Equal(("run-1", "team-T-001", "T-001"), (row.RunId, row.TeamId, row.TaskId));
        using var payload = JsonDocument.Parse(row.Payload!);
        Assert.Equal("write_file", payload.RootElement.GetProperty("tool").GetString());
        Assert.Contains("may not call", payload.RootElement.GetProperty("reason").GetString());

        var result = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.StartsWith("DENIED by policy", result.Result?.ToString());
    }

    [Fact]
    public async Task Allowed_tool_runs_and_long_arguments_are_summarised()
    {
        var model = new ScriptedChatClient(Call("read_file", new { path = new string('a', 500) }), Say("ok"));
        var agent = new FactoryAgents(_ => model, new RoleAllowListPolicy(), _ledger).Create(Scope, ModelTier.Standard, "plan", Tools());

        await agent.RunAsync("go");

        var row = Assert.Single(_ledger.Events, e => e.Kind == LedgerKinds.ToolCall);
        Assert.Equal("allowed", row.Outcome);
        using var payload = JsonDocument.Parse(row.Payload!);
        Assert.Equal("<500 chars>", payload.RootElement.GetProperty("args").GetProperty("path").GetString());
        var result = model.Requests[1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Single();
        Assert.StartsWith("contents of", result.Result?.ToString());
    }
}
