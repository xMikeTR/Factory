using Factory.Core;
using Factory.Harness;
using Microsoft.Extensions.AI;
using Xunit;

namespace Factory.Integration.Tests.Agents;

public class ClaudeCodeChatClientTests
{
    private static readonly AIFunction ReadFile = AIFunctionFactory.Create((string path) => "contents of " + path, "read_file", "Read a file.");

    [Fact]
    public void The_claude_code_provider_needs_no_key_and_uses_Claude_models()
    {
        var g = GatewayOptions.FromEnvironment(new Dictionary<string, string> { ["FACTORY_PROVIDER"] = "claude-code" });

        Assert.Equal(ModelProvider.ClaudeCode, g.Provider);
        Assert.Equal("claude-sonnet-5-5", g.ModelFor(ModelTier.Standard));
        Assert.IsType<ClaudeCodeChatClient>(g.ChatClientFor(ModelTier.Standard));
    }

    [Fact]
    public async Task A_plain_reply_comes_back_as_text_with_usage_and_the_tools_are_offered_as_text()
    {
        IReadOnlyList<string>? seenArgs = null; string? seenStdin = null;
        var client = new ClaudeCodeChatClient("claude-sonnet-5-5", run: (args, stdin, _) =>
        {
            seenArgs = args; seenStdin = stdin;
            return Task.FromResult("""{"result":"All good.","usage":{"input_tokens":10,"output_tokens":3}}""");
        });

        var r = await client.GetResponseAsync([new(ChatRole.System, "Be brief."), new(ChatRole.User, "hi")], new ChatOptions { Tools = [ReadFile] });

        Assert.Equal("All good.", r.Text);
        Assert.Equal(13, r.Usage!.TotalTokenCount);
        Assert.Equal("--tools", seenArgs![3]); Assert.Equal("", seenArgs[4]);
        Assert.Contains("claude-sonnet-5-5", seenArgs);
        var system = seenArgs[seenArgs.ToList().IndexOf("--system-prompt") + 1];
        Assert.Contains("Be brief.", system); Assert.Contains("read_file", system);
        Assert.Equal("User: hi", seenStdin);
    }

    [Fact]
    public async Task A_tool_calls_reply_becomes_function_calls_and_results_go_back_in_the_transcript()
    {
        var client = new ClaudeCodeChatClient("m", run: (_, _, _) => Task.FromResult(
            """{"result":"{\"tool_calls\":[{\"name\":\"read_file\",\"arguments\":{\"path\":\"a.cs\"}},{\"name\":\"nope\",\"arguments\":{}}]}"}"""));

        var r = await client.GetResponseAsync([new(ChatRole.User, "go")], new ChatOptions { Tools = [ReadFile] });

        var call = Assert.Single(r.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>());
        Assert.Equal("read_file", call.Name);
        Assert.Equal("a.cs", call.Arguments!["path"]?.ToString());

        var (_, transcript) = ClaudeCodeChatClient.ToPrompt(null, [new(ChatRole.User, "go"), new(ChatRole.Assistant, [call]),
            new(ChatRole.Tool, [new FunctionResultContent(call.CallId, "file text")])], [ReadFile]);
        Assert.Contains("Tool result (read_file): file text", transcript);
    }

    [Fact]
    public void Text_that_only_mentions_tool_calls_or_names_an_unknown_tool_stays_text()
    {
        Assert.IsType<TextContent>(Assert.Single(ClaudeCodeChatClient.FromReply("I would use tool_calls here.", [ReadFile])));
        Assert.IsType<TextContent>(Assert.Single(ClaudeCodeChatClient.FromReply("""{"tool_calls":[{"name":"nope","arguments":{}}]}""", [ReadFile])));
    }

    [Fact]
    public async Task The_cli_reporting_an_error_is_thrown_not_returned_as_an_answer()
    {
        var client = new ClaudeCodeChatClient("bad", run: (_, _, _) => Task.FromResult("""{"is_error":true,"result":"unknown model"}"""));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new(ChatRole.User, "x")]));
        Assert.Contains("unknown model", e.Message);
    }
}
