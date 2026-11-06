using System.Globalization;
using System.Text.Json;
using Factory.Core;
using Factory.Harness.Policy;

namespace Factory.Harness.Spending;

/// The router's BudgetState for a work item: the worse of the item budget (ledger cost of every run of the item, against
/// `budget.itemCost`) and the user budget (this month's imported spend, against `budget.userMonthlyCost`). A limit that
/// is not configured is not checked. The user budget from a missing or stale import is Soft, with a warning.
public static class BudgetStateReader
{
    public static async Task<(BudgetState State, IReadOnlyList<string> Warnings)> ReadAsync(
        string stateDir, ILedger ledger, CostBudgetPolicy policy, string? item, DateTimeOffset now, CancellationToken ct)
    {
        var state = BudgetState.Ok;
        var warnings = new List<string>();

        if (policy.ItemCost is { } cap && item is not null)
            state = SpendBudget.combine(state, SpendBudget.stateOf(policy.Limits, await ItemCostAsync(stateDir, ledger, item, ct), cap));

        if (policy.UserMonthlyCost is { } userCap)
        {
            var month = now.UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var at = await BudgetStore.LastImportAsync(stateDir, ct);
            var spent = await BudgetStore.UserMonthSpendAsync(stateDir, month, ct);
            var (userState, warning) = SpendBudget.userState(policy.Limits, policy.MaxImportAgeDays, now,
                at is null ? Microsoft.FSharp.Core.FSharpOption<DateTimeOffset>.None : Microsoft.FSharp.Core.FSharpOption<DateTimeOffset>.Some(at.Value),
                spent is null ? Microsoft.FSharp.Core.FSharpOption<Tuple<decimal, decimal>>.None : Microsoft.FSharp.Core.FSharpOption<Tuple<decimal, decimal>>.Some(Tuple.Create(spent.Value, userCap)));
            state = SpendBudget.combine(state, userState);
            if (warning is not null) warnings.Add(warning.Value);
        }
        return (state, warnings);
    }

    /// The reason a new run for the item is refused (exit 4), or null: only a Blocked cost budget refuses; Soft and Downgrade run.
    public static async Task<string?> RefusalAsync(string stateDir, ILedger ledger, CostBudgetPolicy policy, string? item, DateTimeOffset now, CancellationToken ct)
    {
        var (state, _) = await ReadAsync(stateDir, ledger, policy, item, now, ct);
        return state == BudgetState.Blocked
            ? $"{item ?? "the user"} is over its cost budget ({policy.Currency}): raise budget: in .factory/policy.yaml or wait for the next period"
            : null;
    }

    /// The summed `Cost` of every ledger row of every run whose handover names the item; rows without a cost count as zero.
    public static async Task<decimal> ItemCostAsync(string stateDir, ILedger ledger, string item, CancellationToken ct)
    {
        var runs = Path.Combine(stateDir, "runs");
        if (!Directory.Exists(runs)) return 0m;
        decimal total = 0m;
        foreach (var dir in Directory.GetDirectories(runs).Order(StringComparer.Ordinal))
        {
            var handover = Path.Combine(dir, "handover.json");
            if (!File.Exists(handover)) continue;
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(handover, ct));
                if (!doc.RootElement.TryGetProperty("workItem", out var w) || w.GetString() != item) continue;
            }
            catch (JsonException) { continue; }
            await foreach (var e in ledger.ReadAsync(Path.GetFileName(dir), ct)) total += e.Cost ?? 0m;
        }
        return total;
    }
}
