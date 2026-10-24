using System.Text.Json;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class ParityDecisionReportTests
{
    private static string Registry(string pipeline)
    {
        var root = Path.Combine(Path.GetTempPath(), "s2-parity-nd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "ledger"));
        File.WriteAllText(Path.Combine(root, "ledger", "pipeline.jsonl"), pipeline);
        return root;
    }

    private static string Result(string scenario, string commit, string outcome, int ts, string? item = "W-1") =>
        $$"""{"kind":"parity_result","scenario":"{{scenario}}","commit":"{{commit}}","outcome":"{{outcome}}",{{(item is null ? "" : $"\"item\":\"{item}\",")}}"failures":[{"step":1,"key":"button:Save","kind":"missing","msg":"step 1: button Save is missing"}],"ts":{{ts}},"by":"designc"}""" + "\n";

    private static List<JsonElement> Decisions(string root) =>
        File.ReadAllLines(Path.Combine(root, "ledger", "pipeline.jsonl"))
            .Select(l => JsonDocument.Parse(l).RootElement).Where(r => r.GetProperty("kind").GetString() == "needs_decision").ToList();

    [Fact]
    public async Task A_failed_scenario_raises_one_valid_open_needs_decision_once()
    {
        var root = Registry(Result("quote-list", "abc1234def", "fail", 1));

        Assert.Equal(1, await ParityDecisionReport.RaiseAsync(root, DateTimeOffset.UnixEpoch, default));
        Assert.Equal(0, await ParityDecisionReport.RaiseAsync(root, DateTimeOffset.UnixEpoch.AddMinutes(5), default));

        var nd = Assert.Single(Decisions(root));
        Assert.Empty(Factory.Integration.Tests.Support.DesigncSchema.Validate(nd.GetRawText()));
        Assert.Equal(("ND-0001", "open"), (nd.GetProperty("id").GetString(), nd.GetProperty("status").GetString()));
        Assert.Contains("step 1: button Save is missing", nd.GetProperty("question").GetString());
        var p = nd.GetProperty("payload");
        Assert.Equal(["A", "B"], p.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
        Assert.Equal(("quote-list", "abc1234def", "W-1"),
            (p.GetProperty("parity").GetProperty("scenario").GetString(), p.GetProperty("parity").GetProperty("commit").GetString(),
             p.GetProperty("parity").GetProperty("item").GetString()));
    }

    [Fact]
    public async Task Only_the_latest_result_of_a_scenario_counts_and_could_not_run_is_no_decision()
    {
        var root = Registry(
            Result("quote-list", "c1", "fail", 1) + Result("quote-list", "c1", "pass", 2)   // fixed at the same commit
            + Result("search", "c1", "fail", 1) + Result("search", "c2", "fail", 2, item: null) // superseded by c2
            + Result("detail", "c2", "could_not_run", 3));                                   // the environment, not a difference

        Assert.Equal(1, await ParityDecisionReport.RaiseAsync(root, DateTimeOffset.UnixEpoch, default));

        var p = Assert.Single(Decisions(root)).GetProperty("payload").GetProperty("parity");
        Assert.Equal(("search", "c2"), (p.GetProperty("scenario").GetString(), p.GetProperty("commit").GetString()));
        Assert.False(p.TryGetProperty("item", out _));
    }
}
