using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Integration.Tests.Support;
using Factory.S2;
using Xunit;

namespace Factory.Integration.Tests.Workflow;

using Core = Factory.Core.Core;

/// CI runs the heavy suites after a run ends. A failure there is reported back with `factory-s2 ci-report`, blamed on the
/// tasks whose write paths own the failing files, and a repair run re-runs only those tasks with the failure as their brief.
public class CiReportTests
{
    private const string Run = "run-ci";

    private static string Plan(Handover h) => JsonSerializer.Serialize(new
    {
        handover = new { id = h.Id, digest = h.Digest },
        tasks = new object[]
        {
            new { id = "T-001", title = "Data", instructions = "x", elements = new[] { "Pricing Platform/Quotes DB" },
                  writes = new[] { "src/Pricing.Data/**" }, testWrites = new[] { "tests/Pricing.Data.Tests/**" }, dependsOn = Array.Empty<string>(), covers = new[] { "D-0001" },
                  verify = new[] { new { name = "f", kind = "invariant", run = "sh tests/Pricing.Data.Tests/a.sh", timeoutSeconds = 30 } } },
            new { id = "T-002", title = "Api", instructions = "x", elements = new[] { "Pricing Platform/Pricing API" },
                  writes = new[] { "src/Pricing.Api/**" }, testWrites = new[] { "tests/Pricing.Api.Tests/**" }, dependsOn = new[] { "T-001" }, covers = Array.Empty<string>(),
                  verify = new[] { new { name = "g", kind = "invariant", run = "sh tests/Pricing.Api.Tests/b.sh", timeoutSeconds = 30 } } },
        },
    });

    private static async Task<TeamHarness> HarnessAsync()
    {
        var h = await TeamHarness.CreateAsync(new ScriptedChatClient(), new Limits(8, 3));
        var dir = Directory.CreateDirectory(Path.Combine(h.StateDir, "runs", Run)).FullName;
        await File.WriteAllTextAsync(Path.Combine(dir, "handover.json"), Core.HandoverJson(h.Handover));
        await File.WriteAllTextAsync(Path.Combine(dir, "plan.json"), Plan(h.Handover));
        return h;
    }

    [Fact]
    public async Task A_failing_file_is_blamed_on_the_task_that_owns_it()
    {
        using var h = await HarnessAsync();

        var why = await CiReport.RecordAsync(h.Services, Run, "unit tests", "Expected 3 got 2", ["src/Pricing.Api/Quote.cs"], null, "abc1234", default);

        Assert.Null(why);
        var row = Assert.Single(await Rows(h, Run), e => e.Kind == LedgerKinds.CiFailed);
        Assert.Equal("T-002", row.TaskId);
        Assert.Equal("failed", row.Outcome);
        Assert.Contains("Expected 3 got 2", row.Payload);
    }

    [Fact]
    public async Task A_test_file_is_blamed_on_the_task_whose_tests_it_is()
    {
        using var h = await HarnessAsync();
        await CiReport.RecordAsync(h.Services, Run, "unit tests", "log", ["tests/Pricing.Data.Tests/QuoteTests.cs"], null, null, default);
        Assert.Equal("T-001", Assert.Single(await Rows(h, Run), e => e.Kind == LedgerKinds.CiFailed).TaskId);
    }

    [Fact]
    public async Task Nothing_is_guessed_when_no_task_owns_the_files()
    {
        using var h = await HarnessAsync();

        var why = await CiReport.RecordAsync(h.Services, Run, "lint", "log", ["docs/readme.md"], null, null, default);

        Assert.Contains("no task", why);
        Assert.DoesNotContain(await Rows(h, Run), e => e.Kind == LedgerKinds.CiFailed);
    }

    [Fact]
    public async Task A_named_task_must_exist_in_the_plan()
    {
        using var h = await HarnessAsync();
        Assert.Contains("T-009", await CiReport.RecordAsync(h.Services, Run, "lint", "log", [], "T-009", null, default));
        Assert.Null(await CiReport.RecordAsync(h.Services, Run, "lint", "log", [], "T-001", null, default));
    }

    [Fact]
    public async Task A_repair_brief_carries_the_ci_failure_and_only_its_log_tail()
    {
        using var h = await HarnessAsync();
        var log = string.Concat(Enumerable.Repeat("noise\n", 2000)) + "THE REAL ERROR";
        await CiReport.RecordAsync(h.Services, Run, "unit tests", log, ["src/Pricing.Api/Quote.cs"], null, "abc1234", default);

        var repair = await Repair.FromFailedRunAsync(h.Services, Run, default);

        Assert.NotNull(repair);
        Assert.Equal(["T-002"], repair.Feedback.Keys);
        Assert.Contains("unit tests", repair.Feedback["T-002"]);
        Assert.Contains("THE REAL ERROR", repair.Feedback["T-002"]);
        Assert.True(repair.Feedback["T-002"].Length < 3000);
    }

    [Fact]
    public async Task Repair_rounds_are_counted_along_the_chain_and_capped()
    {
        using var h = await HarnessAsync();
        void Chain(string run, string of)
        {
            Directory.CreateDirectory(h.Services.Settings.RunDir(run));
            File.WriteAllText(h.Services.Settings.RepairPath(run), JsonSerializer.Serialize(new RepairFile(of, new Dictionary<string, string>())));
        }
        Assert.Equal(0, Repair.Rounds(h.Services.Settings, Run));
        Chain("fix-1", Run); Chain("fix-2", "fix-1");
        Assert.Equal(1, Repair.Rounds(h.Services.Settings, "fix-1"));
        Assert.Equal(2, Repair.Rounds(h.Services.Settings, "fix-2"));
        Assert.NotNull(Repair.RoundsRefusal(h.Services.Settings, "fix-2", max: 2));
        Assert.Null(Repair.RoundsRefusal(h.Services.Settings, "fix-1", max: 2));
    }

    [Fact]
    public async Task A_corrupt_repair_file_counts_as_a_round_and_gives_no_feedback_instead_of_throwing()
    {
        using var h = await HarnessAsync();
        Directory.CreateDirectory(h.Services.Settings.RunDir("fix-1"));
        File.WriteAllText(h.Services.Settings.RepairPath("fix-1"), "{ not json");
        Assert.Equal(1, Repair.Rounds(h.Services.Settings, "fix-1"));
        Assert.Null(Repair.Feedback(h.Services.Settings, "fix-1", "T-001"));
    }

    private static async Task<List<LedgerEvent>> Rows(TeamHarness h, string run)
    {
        var rows = new List<LedgerEvent>();
        await foreach (var e in h.Services.Ledger.ReadAsync(run)) rows.Add(e);
        return rows;
    }
}
