using System.Text.Json;
using Factory.Core;
using Factory.Harness;
using Factory.Harness.Policy;
using Factory.Harness.Spending;
using Xunit;

namespace Factory.Integration.Tests.Spending;

public sealed class BudgetStateReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bstate-").FullName;
    private readonly JsonlLedger _ledger;
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);

    public BudgetStateReaderTests() => _ledger = new JsonlLedger(Path.Combine(_dir, "ledger.jsonl"));
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static CostBudgetPolicy Policy(decimal? item = null, decimal? user = null) =>
        new(item, user, new BudgetLimits(0.8, 0.9), 2, "GBP");

    private async Task RunOfItemAsync(string run, string item, params decimal?[] costs)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "runs", run));
        await File.WriteAllTextAsync(Path.Combine(_dir, "runs", run, "handover.json"), JsonSerializer.Serialize(new { workItem = item }));
        foreach (var c in costs)
            await _ledger.AppendAsync(new LedgerEvent(run, "S2", "act", LedgerKinds.Turn, TaskId: "T-1", TokensIn: 10, TokensOut: 1, Cost: c));
    }

    private Task<(BudgetState State, IReadOnlyList<string> Warnings)> Read(CostBudgetPolicy p, string item = "W-1") =>
        BudgetStateReader.ReadAsync(_dir, _ledger, p, item, Now, default);

    [Fact]
    public async Task No_cost_limits_means_ok_and_no_warnings()
    {
        await RunOfItemAsync("r1", "W-1", 100m);
        var (state, warnings) = await Read(Policy());
        Assert.Equal(BudgetState.Ok, state);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task Item_cost_sums_the_ledger_cost_of_every_run_of_the_item_and_old_rows_without_cost_count_as_zero()
    {
        await RunOfItemAsync("r1", "W-1", 30m, null);
        await RunOfItemAsync("r2", "W-1", 50m);          // a repair run of the same item
        await RunOfItemAsync("r3", "W-2", 999m);         // another item
        Assert.Equal(BudgetState.Soft, (await Read(Policy(item: 100m))).State);          // 80 of 100
        Assert.Equal(BudgetState.Ok, (await Read(Policy(item: 1000m))).State);
        await RunOfItemAsync("r4", "W-1", 15m);
        Assert.Equal(BudgetState.Downgrade, (await Read(Policy(item: 100m))).State);     // 95 of 100
        await RunOfItemAsync("r5", "W-1", 10m);
        Assert.Equal(BudgetState.Blocked, (await Read(Policy(item: 100m))).State);       // 105
    }

    [Fact]
    public async Task The_user_budget_reads_this_months_imported_spend_and_the_worse_of_the_two_binds()
    {
        await BudgetStore.ImportAsync(_dir, null, "date,model,tokens_in,tokens_out,cost\n2026-11-01,m,1,1,95\n", Now.AddHours(-1), default);
        await RunOfItemAsync("r1", "W-1", 1m);
        var (state, warnings) = await Read(Policy(item: 100m, user: 100m));
        Assert.Equal(BudgetState.Downgrade, state);        // the user's 95 of 100 beats the item's 1 of 100
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task A_stale_or_missing_spend_import_is_soft_with_a_warning_when_a_user_cap_is_set()
    {
        var (none, w1) = await Read(Policy(user: 100m));
        Assert.Equal(BudgetState.Soft, none);
        Assert.Single(w1);

        await BudgetStore.ImportAsync(_dir, null, "date,model,tokens_in,tokens_out,cost\n2026-11-01,m,1,1,1\n", Now.AddDays(-5), default);
        var (stale, w2) = await Read(Policy(user: 100m));
        Assert.Equal(BudgetState.Soft, stale);
        Assert.Contains("5 days", w2[0]);
    }

    [Fact]
    public async Task The_policy_file_reads_the_cost_fields_and_rejects_bad_ones()
    {
        var repo = Directory.CreateTempSubdirectory("repo-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(repo, ".factory"));
            var path = Path.Combine(repo, MergePolicyFile.RelativePath);
            await File.WriteAllTextAsync(path, "budget:\n  itemTokens: 10\n  itemCost: 50\n  userMonthlyCost: 750\n  softAt: 0.7\n  currency: EUR\n");
            var p = CostBudgetPolicyFile.Load(repo);
            Assert.Equal((50m, 750m, 0.7, 0.9, "EUR", 2), (p.ItemCost!.Value, p.UserMonthlyCost!.Value, p.Limits.SoftAt, p.Limits.DowngradeAt, p.Currency, p.MaxImportAgeDays));
            Assert.Equal(CostBudgetPolicy.None, CostBudgetPolicyFile.Load(Directory.CreateTempSubdirectory("empty-").FullName));

            await File.WriteAllTextAsync(path, "budget:\n  softAt: 0.95\n  downgradeAt: 0.9\n");
            Assert.Throws<FactoryRejectedException>(() => CostBudgetPolicyFile.Load(repo));
        }
        finally { Directory.Delete(repo, recursive: true); }
    }

    [Fact]
    public async Task A_new_run_is_refused_only_when_the_cost_budget_is_blocked()
    {
        await RunOfItemAsync("r1", "W-1", 95m);
        Assert.Null(await BudgetStateReader.RefusalAsync(_dir, _ledger, Policy(item: 100m), "W-1", Now, default));      // downgrade still runs
        await RunOfItemAsync("r2", "W-1", 10m);
        var reason = await BudgetStateReader.RefusalAsync(_dir, _ledger, Policy(item: 100m), "W-1", Now, default);
        Assert.NotNull(reason);
        Assert.Contains("W-1", reason);
        Assert.Contains("raise budget:", reason);
    }
}
