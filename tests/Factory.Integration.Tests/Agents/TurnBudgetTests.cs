using Xunit;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Agents;

public class TurnBudgetTests
{
    private static readonly AgentScope Scope = new("run-1", Roles.Implementer, "team-T-001", "T-001");
    private sealed class AllowAll : IToolPolicy
    {
        public PolicyDecision Evaluate(string role, string toolName, IReadOnlyDictionary<string, object?> args) => PolicyDecision.Allow();
    }

    private int _reads, _quality;

    private IList<AITool> Tools() =>
    [
        AIFunctionFactory.Create((string path) => { _reads++; return $"contents of {path}"; }, "read_file"),
        AIFunctionFactory.Create(() => { _quality++; return "quality ok"; }, "check_quality"),
    ];

    private static ChatResponse[] Reads(int n) => Enumerable.Range(1, n).Select(i => Call("read_file", new { path = $"f{i}.cs" })).ToArray();

    private AIAgent Agent(ScriptedChatClient model, TurnBudget budget, MemoryLedger? ledger = null) =>
        new FactoryAgents(_ => model, new AllowAll(), ledger ?? new MemoryLedger())
            .Create(Scope, ModelTier.Standard, "implement", Tools(), budget);

    [Fact]
    public async Task The_turn_ends_at_the_cap_without_another_model_request()
    {
        var model = new ScriptedChatClient([.. Reads(20), Say("never reached")]);
        var budget = new TurnBudget(maxToolCalls: 15, warnAt: 12);

        await Agent(model, budget).RunAsync("go");

        Assert.Equal(15, _reads);
        Assert.True(budget.Exhausted);
        Assert.Equal(15, budget.Calls);
        Assert.Equal(15, model.Requests.Count); // the request after call 15 is never made
    }

    [Fact]
    public async Task A_role_that_only_reads_can_keep_every_result_in_view()
    {
        var model = new ScriptedChatClient([.. Reads(8), Say("done")]);
        var agent = new FactoryAgents(_ => model, new AllowAll(), new MemoryLedger())
            .Create(Scope, ModelTier.Standard, "qa", Tools(), new TurnBudget(30, 24), keepRecentResults: int.MaxValue);

        await agent.RunAsync("go");

        var last = model.Requests[^1].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString());
        Assert.DoesNotContain(last, r => r!.Contains("elided"));
    }

    [Fact]
    public async Task A_turn_under_the_cap_is_not_exhausted()
    {
        var model = new ScriptedChatClient([.. Reads(3), Say("done")]);
        var budget = new TurnBudget();

        await Agent(model, budget).RunAsync("go");

        Assert.False(budget.Exhausted);
        Assert.Equal(3, budget.Calls);
    }

    [Fact]
    public async Task The_result_of_the_twelfth_call_warns_how_many_remain()
    {
        var model = new ScriptedChatClient([.. Reads(13), Say("done")]);

        await Agent(model, new TurnBudget(15, 12)).RunAsync("go");

        string?[] Results(int request) => model.Requests[request].SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString()).ToArray();
        Assert.DoesNotContain("calls left", Results(11)[^1]);          // after call 11
        Assert.Contains("3 calls left", Results(12)[^1]);              // after call 12 (the model sees it in request 13)
        Assert.Contains("declare_done", Results(12)[^1]);
    }

    [Fact]
    public async Task Exempt_tools_are_not_counted()
    {
        var script = Enumerable.Range(0, 30).Select(_ => Call("check_quality")).Append(Say("done")).ToArray();
        var budget = new TurnBudget(15, 12, new HashSet<string> { "check_quality" });

        await Agent(new ScriptedChatClient(script), budget).RunAsync("go");

        Assert.Equal(30, _quality);
        Assert.Equal(0, budget.Calls);
        Assert.False(budget.Exhausted);
    }

    [Fact]
    public async Task Calls_in_the_same_response_beyond_the_cap_are_not_executed()
    {
        var batch = new ChatResponse(new ChatMessage(ChatRole.Assistant,
            Enumerable.Range(1, 5).Select(i => (AIContent)new FunctionCallContent($"b{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{i}.cs" })).ToList()));
        var model = new ScriptedChatClient([.. Reads(2), batch, Say("unreached")]);

        await Agent(model, new TurnBudget(maxToolCalls: 4, warnAt: 3)).RunAsync("go");

        Assert.Equal(4, _reads);
    }
}
