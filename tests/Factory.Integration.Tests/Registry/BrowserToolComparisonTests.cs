using Factory.Harness;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class BrowserToolComparisonTests
{
    private static async Task Task_(JsonlLedger l, string run, string task, string variant, int calls, long tin, long tout, bool passed)
    {
        await l.AppendAsync(new LedgerEvent(run, "S2", "act", LedgerKinds.Turn, TaskId: task, TokensIn: tin, TokensOut: tout,
            Payload: $"{{\"role\":\"s2-implementer\",\"browser\":\"{variant}\",\"tool_calls\":{calls}}}"));
        if (passed) await l.AppendAsync(new LedgerEvent(run, "S2", "task", LedgerKinds.TaskEvent, TaskId: task, Outcome: "Passed", Payload: "{}"));
    }

    [Fact]
    public async Task Variants_are_compared_on_tokens_per_passing_task_tool_calls_and_pass_rate()
    {
        var ledger = new JsonlLedger(Path.Combine(Directory.CreateTempSubdirectory("cmp").FullName, "ledger.jsonl"));
        await Task_(ledger, "r-cli", "T-1", "cli", 6, 1000, 200, passed: true);
        await Task_(ledger, "r-cli", "T-2", "cli", 4, 800, 100, passed: false);
        await Task_(ledger, "r-mcp", "T-1", "mcp", 10, 3000, 400, passed: true);

        var stats = await BrowserToolComparison.CompareAsync(ledger, ["r-cli", "r-mcp"], default);

        var cli = stats.Single(s => s.Variant == "cli");
        Assert.Equal((2, 1, 2100.0, 5.0, 0.5), (cli.Tasks, cli.Passed, cli.TokensPerPassingTask!.Value, cli.ToolCallsPerTask, cli.PassRate));
        var mcp = stats.Single(s => s.Variant == "mcp");
        Assert.Equal((1, 3400.0, 10.0), (mcp.Passed, mcp.TokensPerPassingTask!.Value, mcp.ToolCallsPerTask));
        Assert.Contains("cli", BrowserToolComparison.Render(stats));
    }
}
