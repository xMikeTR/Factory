using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Registry;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using Xunit;

namespace Factory.Integration.Tests.Registry;

public class ItemBudgetReportTests
{
    private static async Task<(string State, JsonlLedger Ledger)> State(params (string Run, string? Item, long In, long Out)[] runs)
    {
        var state = Path.Combine(Path.GetTempPath(), "s2-spend-" + Guid.NewGuid().ToString("N"));
        var ledger = new JsonlLedger(Path.Combine(state, "ledger.jsonl"));
        Directory.CreateDirectory(state);
        foreach (var (run, item, tin, tout) in runs)
        {
            var dir = Directory.CreateDirectory(Path.Combine(state, "runs", run)).FullName;
            await File.WriteAllTextAsync(Path.Combine(dir, "handover.json"), item is null ? "{}" : JsonSerializer.Serialize(new { workItem = item }));
            await ledger.AppendAsync(new LedgerEvent(run, "S2", "act", LedgerKinds.Turn, TokensIn: tin, TokensOut: tout));
        }
        return (state, ledger);
    }

    private static BudgetPolicy Policy(long? limit) =>
        new(limit is { } l ? FSharpOption<long>.Some(l) : FSharpOption<long>.None, MapModule.Empty<string, long>());

    [Fact]
    public async Task Spend_is_every_runs_tokens_for_the_item_the_handover_names_a_repair_run_included()
    {
        var (state, ledger) = await State(("r1", "W-1", 100, 50), ("r1-r1", "W-1", 10, 5), ("r2", "W-2", 7, 3), ("r3", null, 999, 999));

        var spend = await ItemBudgetReport.SpendAsync(state, ledger, default);

        Assert.Equal(new Dictionary<string, long> { ["W-1"] = 165, ["W-2"] = 10 }, spend);
    }

    [Fact]
    public async Task Readings_exist_only_for_items_with_a_limit_and_flag_the_exceeded_one()
    {
        var readings = ItemBudgetReport.Readings(Policy(100), new Dictionary<string, long> { ["W-1"] = 165, ["W-2"] = 10 });

        Assert.Equal([("W-1", true), ("W-2", false)], readings.Select(r => (r.Item, r.Status.IsExceeded)));
        Assert.Empty(ItemBudgetReport.Readings(Policy(null), new Dictionary<string, long> { ["W-1"] = 165 }));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_reading_is_written_as_an_item_budget_row_once_and_reaches_the_pipeline_stream()
    {
        var registry = Path.Combine(Path.GetTempPath(), "s2-budget-reg-" + Guid.NewGuid().ToString("N"));
        var readings = ItemBudgetReport.Readings(Policy(100), new Dictionary<string, long> { ["W-1"] = 165 });

        await ItemBudgetReport.WriteAsync(registry, readings, DateTimeOffset.UnixEpoch, default);
        await ItemBudgetReport.WriteAsync(registry, readings, DateTimeOffset.UnixEpoch.AddMinutes(5), default);

        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "pipeline.jsonl")));
        Assert.Empty(Factory.Integration.Tests.Support.DesigncSchema.Validate(line));
        using var row = JsonDocument.Parse(line);
        var r = row.RootElement;
        Assert.Equal(("item_budget", "W-1", "exceeded", 100, 165),
            (r.GetProperty("kind").GetString(), r.GetProperty("item").GetString(), r.GetProperty("status").GetString(),
             r.GetProperty("limit_tokens").GetInt32(), r.GetProperty("spent_tokens").GetInt32()));
    }

    [Fact]
    public async Task An_exceeded_item_refuses_a_new_task_and_asks_for_a_person_once()
    {
        var (state, ledger) = await State(("r1", "W-1", 100, 50));
        var registry = Path.Combine(Path.GetTempPath(), "s2-budget-gate-" + Guid.NewGuid().ToString("N"));

        var refusal = await ItemBudgetReport.GateAsync(state, ledger, Policy(100), "W-1", registry, DateTimeOffset.UnixEpoch, default);
        await ItemBudgetReport.GateAsync(state, ledger, Policy(100), "W-1", registry, DateTimeOffset.UnixEpoch.AddMinutes(5), default);

        Assert.Equal("W-1 has spent 150 of 100 tokens: raise budget: in .factory/policy.yaml or decide in the digest", refusal);
        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(registry, "ledger", "pipeline.jsonl")));
        Assert.Empty(Factory.Integration.Tests.Support.DesigncSchema.Validate(line));
        using var row = JsonDocument.Parse(line);
        Assert.Equal(("item_awaiting_human", "W-1"),
            (row.RootElement.GetProperty("kind").GetString(), row.RootElement.GetProperty("item").GetString()));
    }

    [Fact]
    public async Task A_within_or_unlimited_item_may_start_and_writes_nothing()
    {
        var (state, ledger) = await State(("r1", "W-1", 10, 5));
        var registry = Path.Combine(Path.GetTempPath(), "s2-budget-gate-" + Guid.NewGuid().ToString("N"));

        Assert.Null(await ItemBudgetReport.GateAsync(state, ledger, Policy(100), "W-1", registry, DateTimeOffset.UnixEpoch, default));
        Assert.Null(await ItemBudgetReport.GateAsync(state, ledger, Policy(null), "W-1", registry, DateTimeOffset.UnixEpoch, default));
        Assert.False(File.Exists(Path.Combine(registry, "ledger", "pipeline.jsonl")));
    }
}
