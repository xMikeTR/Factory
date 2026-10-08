using Xunit;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Microsoft.Extensions.AI;
using static Factory.Integration.Tests.Support.ScriptedChatClient;

namespace Factory.Integration.Tests.Agents;

public class ToolResultTrimmerTests
{
    private static List<ChatMessage> Conversation(int calls)
    {
        var msgs = new List<ChatMessage> { new(ChatRole.User, "the brief") };
        for (var i = 1; i <= calls; i++)
        {
            msgs.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"c{i}", "read_file", new Dictionary<string, object?> { ["path"] = $"f{i}.cs" })]));
            msgs.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"c{i}", new string('x', 1000 + i))]));
        }
        return msgs;
    }

    private static string?[] Results(IEnumerable<ChatMessage> msgs) =>
        msgs.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString()).ToArray();

    [Fact]
    public void Results_older_than_the_last_five_become_stubs()
    {
        var trimmed = ToolResultTrimmer.Trim(Conversation(8), keepRecent: 5);

        var results = Results(trimmed);
        Assert.Equal(8, results.Length);
        Assert.All(results.Take(3), r => Assert.StartsWith("[read_file result elided", r));
        Assert.All(results.Skip(3), r => Assert.StartsWith("xxx", r));
        Assert.Contains("1001 chars", results[0]);
    }

    [Fact]
    public void The_brief_and_assistant_messages_are_untouched_and_the_input_is_not_mutated()
    {
        var original = Conversation(8);
        var before = Results(original);

        var trimmed = ToolResultTrimmer.Trim(original, keepRecent: 5);

        Assert.Equal("the brief", trimmed[0].Text);
        Assert.Equal(original.Count, trimmed.Count);
        Assert.Equal(before, Results(original));
        Assert.Equal(8, trimmed.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Count());
    }

    [Fact]
    public void Five_or_fewer_results_are_left_alone()
    {
        Assert.Equal(Results(Conversation(5)), Results(ToolResultTrimmer.Trim(Conversation(5), keepRecent: 5)));
    }

    [Fact]
    public async Task The_model_sees_trimmed_history_when_wired_through_FactoryAgents()
    {
        var script = Enumerable.Range(1, 8).Select(i => Call("read_file", new { path = $"f{i}.cs" })).Append(Say("done")).ToArray();
        var model = new ScriptedChatClient(script);
        IList<AITool> tools = [AIFunctionFactory.Create((string path) => new string('y', 2000), "read_file")];
        var agent = new FactoryAgents(_ => model, new RoleAllowListPolicy(), new MemoryLedger())
            .Create(new AgentScope("run-1", Roles.TaskPlanner, "team-T-001", "T-001"), ModelTier.Standard, "plan", tools);

        await agent.RunAsync("go");

        var last = Results(model.Requests[^1].ToList());
        Assert.Equal(8, last.Length);
        Assert.Equal(3, last.Count(r => r!.StartsWith("[read_file result elided")));
    }
}
