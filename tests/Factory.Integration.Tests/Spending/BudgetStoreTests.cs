using Factory.Core;
using Factory.Harness.Spending;
using Microsoft.FSharp.Collections;
using Xunit;

namespace Factory.Integration.Tests.Spending;

public sealed class BudgetStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("budget-").FullName;
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string Prices = "model,in_per_mtok,out_per_mtok,currency,source,effective\nclaude-x,3,15,GBP,inv,2026-10-01\n";
    private const string Spend = "date,model,tokens_in,tokens_out,cost\n2026-11-01,claude-x,1000,100,10\n2026-11-02,claude-x,1000,100,5\n2026-10-30,claude-x,1,1,99\n";

    private static ModelEntry Entry(string id, string name, decimal pin = 1m) =>
        new(id, "claude", Protocol.AnthropicVertex, name, ModelTier.Standard, new Price(pin, pin), SetModule.Empty<Capability>(), 1000, SetModule.Empty<DataClass>(), Tuple.Create(1.0, 1.0));

    [Fact]
    public async Task An_import_writes_prices_and_monthly_spend_and_nothing_when_a_file_is_bad()
    {
        var bad = await BudgetStore.ImportAsync(_dir, Prices, "date,model\nx,y\n", Now, default);
        Assert.NotEmpty(bad.Errors);
        Assert.False(Directory.Exists(Path.Combine(_dir, "budget")));          // all or nothing

        var ok = await BudgetStore.ImportAsync(_dir, Prices, Spend, Now, default);
        Assert.Empty(ok.Errors);
        Assert.Equal(1, ok.PricesAdded);
        Assert.Equal(3, ok.SpendRows);
        Assert.Equal(15m, await BudgetStore.UserMonthSpendAsync(_dir, "2026-11", default));
        Assert.Equal(99m, await BudgetStore.UserMonthSpendAsync(_dir, "2026-10", default));
        Assert.Null(await BudgetStore.UserMonthSpendAsync(_dir, "2026-09", default));
        Assert.Equal(Now, await BudgetStore.LastImportAsync(_dir, default));
    }

    [Fact]
    public async Task Prices_are_append_only_and_a_repeated_import_adds_nothing()
    {
        await BudgetStore.ImportAsync(_dir, Prices, null, Now, default);
        var again = await BudgetStore.ImportAsync(_dir, Prices, null, Now.AddDays(1), default);
        Assert.Equal(0, again.PricesAdded);
        var newer = await BudgetStore.ImportAsync(_dir, "model,in_per_mtok,out_per_mtok,currency,source,effective\nclaude-x,4,20,GBP,inv,2026-11-01\n", null, Now.AddDays(2), default);
        Assert.Equal(1, newer.PricesAdded);
        Assert.Equal(2, (await BudgetStore.LoadPricesAsync(_dir, default)).Count);
    }

    [Fact]
    public async Task Catalog_prices_come_from_the_import_by_model_name_and_unmatched_models_keep_the_placeholder()
    {
        await BudgetStore.ImportAsync(_dir, Prices + "claude-x,4,20,GBP,inv,2026-11-01\n", null, Now, default);
        var prices = await BudgetStore.LoadPricesAsync(_dir, default);
        var (models, unmatched) = BudgetStore.ApplyPrices([Entry("std", "claude-x"), Entry("other", "unknown-model", 7m)], prices, DateOnly.FromDateTime(Now.UtcDateTime));

        Assert.Equal(4m, models[0].Price.InPerMTok);                         // the latest in force today
        Assert.Equal(20m, models[0].Price.OutPerMTok);
        Assert.Equal(7m, models[1].Price.InPerMTok);                         // placeholder kept
        Assert.Equal(["other"], unmatched);
    }

    [Fact]
    public async Task A_turn_is_costed_with_the_price_in_force_on_its_date_and_unknown_models_have_no_cost()
    {
        await BudgetStore.ImportAsync(_dir, Prices + "claude-x,4,20,GBP,inv,2026-11-01\n", null, Now, default);
        var prices = await BudgetStore.LoadPricesAsync(_dir, default);
        Assert.Equal(0.021m, BudgetStore.CostOf(prices, "claude-x", 2000, 1000, new DateOnly(2026, 10, 20)));   // 3/15 per M
        Assert.Equal(0.028m, BudgetStore.CostOf(prices, "claude-x", 2000, 1000, new DateOnly(2026, 11, 20)));   // 4/20 per M
        Assert.Null(BudgetStore.CostOf(prices, "nope", 1, 1, new DateOnly(2026, 11, 1)));
        Assert.Null(BudgetStore.CostOf(prices, "claude-x", 1, 1, new DateOnly(2026, 9, 1)));
    }
}
