using System.Text.Json;
using Factory.Harness.Registry;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class ItemJudgeReportTests
{
    [Fact]
    public async Task A_judgement_is_one_valid_item_judge_row_written_once()
    {
        var registry = Path.Combine(Path.GetTempPath(), "s2-judge-reg-" + Guid.NewGuid().ToString("N"));
        var j = new ItemJudgement("W-1", "ux", 3.5, 4.0, "abc123", "s2-ux-judge", "deadbeef");

        await ItemJudgeReport.WriteAsync(registry, j, DateTimeOffset.UnixEpoch, default);
        await ItemJudgeReport.WriteAsync(registry, j, DateTimeOffset.UnixEpoch.AddMinutes(5), default);

        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "pipeline.jsonl")));
        Assert.Empty(Factory.Integration.Tests.Support.DesigncSchema.Validate(line));
        using var row = JsonDocument.Parse(line);
        var r = row.RootElement;
        Assert.Equal(("item_judge", "W-1", "ux", 3.5, 4.0, "fail", "abc123"),
            (r.GetProperty("kind").GetString(), r.GetProperty("item").GetString(), r.GetProperty("judge").GetString(),
             r.GetProperty("score").GetDouble(), r.GetProperty("threshold").GetDouble(), r.GetProperty("outcome").GetString(),
             r.GetProperty("commit").GetString()));
    }

    [Fact]
    public async Task A_missing_commit_is_null_and_still_valid()
    {
        var registry = Path.Combine(Path.GetTempPath(), "s2-judge-reg-" + Guid.NewGuid().ToString("N"));
        await ItemJudgeReport.WriteAsync(registry, new ItemJudgement("W-1", "ux", 4.5, 4.0, null, null, null), DateTimeOffset.UnixEpoch, default);

        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "pipeline.jsonl")));
        Assert.Empty(Factory.Integration.Tests.Support.DesigncSchema.Validate(line));
        Assert.Contains("\"outcome\":\"pass\"", line);
    }
}
