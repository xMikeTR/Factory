using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Factory.Harness.Registry;

/// A judge's score for one work item at one commit, written as Designc's `item_judge` row. A judge is a gradient:
/// the digest shows the score on the matching `judge` done_when entry and a fail never fails or completes the item.
public sealed record ItemJudgement(
    string Item, string Judge, double Score, double Threshold, string? Commit, string? JudgeVersion, string? RubricHash);

public static class ItemJudgeReport
{
    public const string RunStream = "judge";

    /// One row per (item, judge, commit, score, rubric): the same judgement is written once, a new score is a new row.
    public static async Task<int> WriteAsync(string registryPath, ItemJudgement j, DateTimeOffset now, CancellationToken ct)
    {
        await RunLedgerStream.AppendAsync(registryPath, RunStream, [Row(j, now)], ct);
        await LedgerFold.RunAsync(registryPath, ct);
        return 1;
    }

    private static RegistryEvent Row(ItemJudgement j, DateTimeOffset now)
    {
        var id = "s2-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"judge\n{j.Item}\n{j.Judge}\n{j.Commit}\n{j.Score:R}\n{j.Threshold:R}\n{j.RubricHash}")))[..32];
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", "item_judge");
            w.WriteString("item", j.Item);
            w.WriteString("judge", j.Judge);
            w.WriteNumber("score", j.Score);
            w.WriteNumber("threshold", j.Threshold);
            w.WriteString("outcome", j.Score >= j.Threshold ? "pass" : "fail");
            if (j.Commit is null) w.WriteNull("commit"); else w.WriteString("commit", j.Commit);
            if (j.JudgeVersion is not null) w.WriteString("judge_version", j.JudgeVersion);
            if (j.RubricHash is not null) w.WriteString("rubric_hash", j.RubricHash);
            w.WriteString("by", RegistryLedgerProjector.By);
            w.WriteString("system", "S2");
            w.WriteNumber("schema_version", 1);
            w.WriteNumber("ts", now.ToUnixTimeMilliseconds());
            w.WriteString("event_id", id);
            w.WriteEndObject();
        }
        return new RegistryEvent("pipeline.jsonl", id, Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
