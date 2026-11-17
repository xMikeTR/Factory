using System.Text.Json;
using Factory.Harness;
using Factory.Harness.Registry;
using Factory.Integration.Tests.Support;
using Xunit;

namespace Factory.Integration.Tests.Registry;

/// F8's Done when: a fixture run's projected rows give the inputs of designc's three System 3 numbers (time to signal,
/// escape rate, veto and recovery). Fixtures/system3/run.jsonl is the hand-written run ledger; projected.jsonl is the
/// golden output that designc's D7 part 2 copies as its fixture. Regenerate it with FACTORY_UPDATE_GOLDEN=1.
public class System3FixtureTests
{
    private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "system3");
    private static readonly HandoverRef Handover = new("20261117-s3", [], "W-ui", new Dictionary<string, string> { ["App/Summary"] = "summary", ["App/Portal"] = "bordereaux" });

    private static async Task<List<LedgerEvent>> ReadRunAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "s3-" + Guid.NewGuid().ToString("N"), "ledger.jsonl");
        var ledger = new JsonlLedger(path);
        Directory.CreateDirectory(Path.GetDirectoryName(ledger.RunFile("run-s3"))!);
        File.Copy(Path.Combine(Dir, "run.jsonl"), ledger.RunFile("run-s3"));
        var rows = new List<LedgerEvent>();
        await foreach (var e in ledger.ReadAsync("run-s3")) rows.Add(e);
        return rows;
    }

    private static async Task<List<RegistryEvent>> ProjectAsync() =>
        RegistryLedgerProjector.Project(await ReadRunAsync(), Handover, new(5, 3)).ToList();

    private static List<JsonElement> Verdicts(IEnumerable<RegistryEvent> rows) =>
        rows.Where(r => r.Line.Contains("\"kind\":\"stage_verdict\"")).Select(r => JsonDocument.Parse(r.Line).RootElement.Clone()).ToList();

    private static string S(JsonElement e, string p) => e.GetProperty(p).GetString()!;

    [Fact]
    public async Task Every_projected_row_validates_against_the_pinned_schema_and_the_golden_file_is_current()
    {
        var rows = await ProjectAsync();
        foreach (var r in rows) Assert.Empty(DesigncSchema.Validate(r.Line));

        var golden = string.Concat(rows.Select(r => r.Line + "\n"));
        if (Environment.GetEnvironmentVariable("FACTORY_UPDATE_GOLDEN") == "1")
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "Factory.Integration.Tests", "Fixtures", "system3"))) dir = dir.Parent;
            File.WriteAllText(Path.Combine(dir!.FullName, "tests", "Factory.Integration.Tests", "Fixtures", "system3", "projected.jsonl"), golden);
        }
        Assert.Equal(File.ReadAllText(Path.Combine(Dir, "projected.jsonl")), golden);
    }

    [Fact]
    public async Task Each_stage_has_a_verdict_with_a_change_ts_which_is_what_time_to_signal_needs()
    {
        var verdicts = Verdicts(await ProjectAsync());
        foreach (var stage in new[] { "ui-code", "ui-visual", "acceptance", "holdout", "review", "qa" })
        {
            var of = verdicts.Where(v => S(v, "stage") == stage).ToList();
            Assert.NotEmpty(of);
            Assert.Contains(of, v => v.GetProperty("change_ts").ValueKind == JsonValueKind.Number);
        }
        Assert.All(verdicts.Where(v => v.GetProperty("change_ts").ValueKind == JsonValueKind.Number), v =>
            Assert.True(v.GetProperty("change_ts").GetInt64() <= v.GetProperty("ts").GetInt64()));
    }

    [Fact]
    public async Task The_rows_carry_the_escape_rate_inputs_a_fail_per_caught_stage_and_the_veto_and_recovery_inputs()
    {
        var rows = await ProjectAsync();
        var verdicts = Verdicts(rows);
        // caught(S): distinct (run, task, stage) with a fail
        foreach (var stage in new[] { "ui-visual", "acceptance", "holdout", "qa" })
            Assert.Contains(verdicts, v => S(v, "stage") == stage && S(v, "outcome") == "fail");
        Assert.Equal(("T-A", "bordereaux"), verdicts.Where(v => S(v, "stage") == "ui-visual" && S(v, "outcome") == "fail").Select(v => (S(v, "task"), S(v, "screen"))).Single());

        // review: a fail round, then a pass round for the same task; its task row ends passed (recovery)
        var review = verdicts.Where(v => S(v, "stage") == "review" && S(v, "task") == "T-A").ToList();
        Assert.Equal([("fail", 1), ("pass", 2)], review.Select(v => (S(v, "outcome"), v.GetProperty("round").GetInt32())));
        var finalTask = rows.Select(r => JsonDocument.Parse(r.Line).RootElement).Last(r => S(r, "kind") == "task" && S(r, "task") == "T-A");
        Assert.Equal("passed", S(finalTask, "status"));

        // qa: a run-level fail
        var qa = verdicts.Single(v => S(v, "stage") == "qa");
        Assert.Equal("fail", S(qa, "outcome"));
        Assert.False(qa.TryGetProperty("task", out _));
    }

    [Fact]
    public async Task No_verdict_contains_text_from_the_source_rows_detail_tail_feedback_or_trace()
    {
        Assert.Contains("LEAKSECRET", await File.ReadAllTextAsync(Path.Combine(Dir, "run.jsonl")));
        foreach (var v in Verdicts(await ProjectAsync())) Assert.DoesNotContain("LEAKSECRET", v.GetRawText());
    }
}
